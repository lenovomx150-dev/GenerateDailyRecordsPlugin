using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Organization;
using Microsoft.Crm.Sdk.Messages;

namespace GenerateDailyRecordsPlugin
{
    internal sealed class DailyRecordService
    {
        private readonly IOrganizationService service;
        private readonly ITracingService trace;
        private readonly DateTime today;
        private readonly Guid emailSenderId;
        private readonly Dictionary<string, int> optionValueCache = new Dictionary<string, int>();
        private readonly HashSet<string> tracedLookupFields = new HashSet<string>();
        // Kept until the end of the run so the important resident decisions survive Dataverse trace truncation.
        private readonly List<string> unitCensusResidentRunSummary = new List<string>();
        private readonly List<string> batchFailureSummary = new List<string>();
        private int omittedBatchFailureCount;
        internal DailyRecordService(IOrganizationService service, ITracingService trace, DateTime today, Guid emailSenderId) { this.service = service; this.trace = trace; this.today = today.Date; this.emailSenderId = emailSenderId; }
        internal void Generate()
        {
            DeactivatePreviousDayOfCareRecords();
            ActivateTodaysDayOfCareRecords();
            var facilities = Query(SchemaNames.Entities.Facility, new ColumnSet(SchemaNames.Fields.Name));
            foreach (var facility in facilities)
            {
                try { GenerateFacility(facility); }
                catch (Exception ex) { trace.Trace("Facility {0} failed. Exception: {1}", facility.Id, ex); }
            }
            trace.Trace("UNIT CENSUS RESIDENT RUN SUMMARY ({0:yyyy-MM-dd})\n{1}", today, unitCensusResidentRunSummary.Count == 0 ? "No facilities processed." : string.Join("\n", unitCensusResidentRunSummary));
            trace.Trace("BATCH FAILURE SUMMARY ({0:yyyy-MM-dd})\n{1}", today, batchFailureSummary.Count == 0 ? "No batch failures." : string.Join("\n", batchFailureSummary) + (omittedBatchFailureCount == 0 ? "" : "\nAdditional batch failures omitted: " + omittedBatchFailureCount + "."));
        }
        private void GenerateFacility(Entity facility)
        {
            var facilityName = facility.GetAttributeValue<string>(SchemaNames.Fields.Name) ?? facility.Id.ToString();
            var residentSummary = new FacilityResidentSummary(facilityName, facility.Id);
            trace.Trace("Processing facility '{0}' ({1}) for {2:yyyy-MM-dd}.", facilityName, facility.Id, today);
            var censusName = NameHelper.Census(facilityName, today);
            var existing = Query(SchemaNames.Entities.DailyCensus, new ColumnSet(false), Filter(SchemaNames.Fields.Facility, ConditionOperator.Equal, facility.Id), Filter(SchemaNames.Fields.Name, ConditionOperator.Equal, censusName)).FirstOrDefault();
            Guid dailyId;
            EntityReference daily;
            if (existing == null)
            {
                var census = new Entity(SchemaNames.Entities.DailyCensus); census[SchemaNames.Fields.Name] = censusName; census[SchemaNames.Fields.Facility] = facility.ToEntityReference();
                dailyId = service.Create(census);
                daily = new EntityReference(SchemaNames.Entities.DailyCensus, dailyId);
            }
            else
            {
                dailyId = existing.Id;
                daily = existing.ToEntityReference();
            }
            var areas = Query(SchemaNames.Entities.LivingArea, new ColumnSet(SchemaNames.Fields.Name), Filter(SchemaNames.Fields.Facility, ConditionOperator.Equal, facility.Id));
            var existingUnits = Query(SchemaNames.Entities.UnitCensus, new ColumnSet(SchemaNames.Fields.LivingArea), Filter(SchemaNames.Fields.DailyCensus, ConditionOperator.Equal, dailyId));
            var units = existingUnits.Where(x => x.Contains(SchemaNames.Fields.LivingArea)).GroupBy(x => x.GetAttributeValue<EntityReference>(SchemaNames.Fields.LivingArea).Id).ToDictionary(x => x.Key, x => x.First().ToEntityReference());
            foreach (var area in areas)
            {
                if (units.ContainsKey(area.Id))
                {
                    continue;
                }
                var unit = new Entity(SchemaNames.Entities.UnitCensus); unit[SchemaNames.Fields.Name] = NameHelper.Census(area.GetAttributeValue<string>(SchemaNames.Fields.Name) ?? area.Id.ToString(), today); unit[SchemaNames.Fields.DailyCensus] = daily; unit[SchemaNames.Fields.Facility] = facility.ToEntityReference(); unit[SchemaNames.Fields.LivingArea] = area.ToEntityReference();
                var unitId = service.Create(unit);
                units.Add(area.Id, new EntityReference(SchemaNames.Entities.UnitCensus, unitId));
            }
            var records = Query(SchemaNames.Entities.FacilityRecord, new ColumnSet(SchemaNames.Fields.FacilityRecordFacility, SchemaNames.Fields.CurrentLivingArea, SchemaNames.Fields.FacilityRecordJuvenile, SchemaNames.Fields.PlacingCounty), Filter(SchemaNames.Fields.FacilityRecordFacility, ConditionOperator.Equal, facility.Id), Filter(SchemaNames.Fields.StateCode, ConditionOperator.Equal, 0));
            var existingResidents = Query(SchemaNames.Entities.UnitCensusResident, new ColumnSet(SchemaNames.Fields.FacilityRecord), Filter(SchemaNames.Fields.DailyCensus, ConditionOperator.Equal, dailyId));
            var existingResidentRecordIds = new HashSet<Guid>(existingResidents.Where(x => x.Contains(SchemaNames.Fields.FacilityRecord)).Select(x => x.GetAttributeValue<EntityReference>(SchemaNames.Fields.FacilityRecord).Id));
            var existingCare = Query(SchemaNames.Entities.DayOfCare, new ColumnSet(SchemaNames.Fields.FacilityRecord), Filter(SchemaNames.Fields.Date, ConditionOperator.On, today));
            var existingCareRecordIds = new HashSet<Guid>(existingCare.Where(x => x.Contains(SchemaNames.Fields.FacilityRecord)).Select(x => x.GetAttributeValue<EntityReference>(SchemaNames.Fields.FacilityRecord).Id));
            var juvenileIds = records.Where(r => r.Contains(SchemaNames.Fields.FacilityRecordJuvenile)).Select(r => r.GetAttributeValue<EntityReference>(SchemaNames.Fields.FacilityRecordJuvenile).Id).Distinct().ToArray();
            var juveniles = juvenileIds.Length == 0 ? new List<Entity>() : Query(SchemaNames.Entities.Juvenile, new ColumnSet(SchemaNames.Fields.FullName, SchemaNames.Fields.JuvenileBjjsId), FilterIn("ucm_offenderid", juvenileIds));
            var bjjsByJuvenile = juveniles.ToDictionary(j => j.Id, j => j.GetAttributeValue<string>(SchemaNames.Fields.JuvenileBjjsId));
            var residentNameByJuvenile = juveniles.ToDictionary(j => j.Id, j => j.GetAttributeValue<string>(SchemaNames.Fields.FullName));
            var absences = Query(SchemaNames.Entities.TemporaryAbsence, new ColumnSet(SchemaNames.Fields.FacilityRecordMovement, SchemaNames.Fields.Purpose, SchemaNames.Fields.AbsenceStart, SchemaNames.Fields.AbsenceEnd), Filter(SchemaNames.Fields.StateCode, ConditionOperator.Equal, 0), Filter(SchemaNames.Fields.AbsenceStart, ConditionOperator.OnOrBefore, today));
            var absenceByRecord = absences.Where(x => x.Contains(SchemaNames.Fields.FacilityRecordMovement) && (!x.Contains(SchemaNames.Fields.AbsenceEnd) || x.GetAttributeValue<DateTime>(SchemaNames.Fields.AbsenceEnd).Date >= today)).GroupBy(x => x.GetAttributeValue<EntityReference>(SchemaNames.Fields.FacilityRecordMovement).Id).ToDictionary(x => x.Key, x => x.First());
            var residentCreates = new List<Entity>(); var careCreates = new List<Entity>();
            foreach (var record in records)
            {
                Entity absence; absenceByRecord.TryGetValue(record.Id, out absence); var area = record.GetAttributeValue<EntityReference>(SchemaNames.Fields.CurrentLivingArea); EntityReference unit;
                var juvenileReference = record.GetAttributeValue<EntityReference>(SchemaNames.Fields.FacilityRecordJuvenile);
                if (area != null && units.TryGetValue(area.Id, out unit))
                {
                    if (existingResidentRecordIds.Contains(record.Id))
                    {
                        residentSummary.AlreadyExisted++;
                    }
                    else
                    {
                        var resident = new Entity(SchemaNames.Entities.UnitCensusResident); resident[SchemaNames.Fields.Name] = record.Id + " - " + today.ToString("MM/dd/yyyy"); resident[SchemaNames.Fields.Date] = today; resident[SchemaNames.Fields.UnitCensus] = unit; resident[SchemaNames.Fields.DailyCensus] = daily; resident[SchemaNames.Fields.FacilityRecord] = record.ToEntityReference(); resident[SchemaNames.Fields.Facility] = facility.ToEntityReference(); resident[SchemaNames.Fields.LivingArea] = area; if (record.Contains(SchemaNames.Fields.FacilityRecordJuvenile)) resident[SchemaNames.Fields.UnitCensusResidentJuvenile] = record[SchemaNames.Fields.FacilityRecordJuvenile]; if (absence != null && absence.Contains(SchemaNames.Fields.Purpose)) resident[SchemaNames.Fields.Purpose] = absence[SchemaNames.Fields.Purpose]; residentCreates.Add(resident);
                        residentSummary.QueuedFacilityRecordIds.Add(record.Id);
                    }
                }
                else
                {
                    residentSummary.UnmatchedLivingAreas.Add(record.Id + " / " + ReferenceId(area));
                }
                var daysAway = absence == null ? 0 : (today - absence.GetAttributeValue<DateTime>(SchemaNames.Fields.AbsenceStart).Date).Days + 1; if (daysAway >= 7) continue;
                if (existingCareRecordIds.Contains(record.Id)) continue;
                var juvenile = juvenileReference; string bjjsId = null; string residentName = null; if (juvenile != null) { bjjsByJuvenile.TryGetValue(juvenile.Id, out bjjsId); residentNameByJuvenile.TryGetValue(juvenile.Id, out residentName); }
                if (string.IsNullOrWhiteSpace(residentName) && juvenile != null) residentName = juvenile.Name; if (string.IsNullOrWhiteSpace(residentName)) residentName = bjjsId; if (string.IsNullOrWhiteSpace(residentName)) residentName = record.Id.ToString(); var care = new Entity(SchemaNames.Entities.DayOfCare); care[SchemaNames.Fields.Name] = NameHelper.Census(residentName, today); care[SchemaNames.Fields.FacilityRecord] = record.ToEntityReference(); care[SchemaNames.Fields.Date] = today; if (record.Contains(SchemaNames.Fields.FacilityRecordFacility)) care[SchemaNames.Fields.Facility] = record[SchemaNames.Fields.FacilityRecordFacility]; if (area != null) care[SchemaNames.Fields.LivingArea] = area;
                var billingLabel = daysAway == 6 ? "Non-Billable" : "Billable";
                var billingValue = FindOptionValue(SchemaNames.Entities.DayOfCare, SchemaNames.Fields.Billing, billingLabel);
                if (billingValue.HasValue) care[SchemaNames.Fields.Billing] = new OptionSetValue(billingValue.Value);
                else trace.Trace("Day of Care billing choice '{0}' was not found. The billing field will be left blank for Facility Record {1}.", billingLabel, record.Id);
                var purposeValue = absence == null ? null : absence.GetAttributeValue<OptionSetValue>(SchemaNames.Fields.Purpose);
                var reasonLabel = absence == null ? "In Care" : purposeValue == null ? null : PurposeText(absence);
                var reasonValue = string.IsNullOrWhiteSpace(reasonLabel) ? (int?)null : FindOptionValue(SchemaNames.Entities.DayOfCare, SchemaNames.Fields.CensusCode, reasonLabel);
                if (reasonValue.HasValue) care[SchemaNames.Fields.CensusCode] = new OptionSetValue(reasonValue.Value);
                else trace.Trace("Day of Care reason was not set for Facility Record {0}. Source purpose={1}; matching Day of Care choice='{2}'.", record.Id, PurposeDetails(absence), reasonLabel ?? "(purpose blank)");
                if (juvenile != null) care[SchemaNames.Fields.DayOfCareBjjsId] = bjjsId; if (record.Contains(SchemaNames.Fields.PlacingCounty)) care[SchemaNames.Fields.PlacingCounty] = record[SchemaNames.Fields.PlacingCounty];
                ApplyExceptionDetails(care);
                careCreates.Add(care);
            }
            var createdResidents = CreateBatch(residentCreates, "Unit Census Resident"); var createdCare = CreateBatch(careCreates, "Day of Care");
            var createdResidentRecordIds = new HashSet<Guid>(createdResidents.Where(x => x.Contains(SchemaNames.Fields.FacilityRecord)).Select(x => x.GetAttributeValue<EntityReference>(SchemaNames.Fields.FacilityRecord).Id));
            residentSummary.Created = residentSummary.QueuedFacilityRecordIds.Count(id => createdResidentRecordIds.Contains(id));
            residentSummary.Failed = residentSummary.QueuedFacilityRecordIds.Count - residentSummary.Created;
            unitCensusResidentRunSummary.Add(residentSummary.ToTraceLine());
            SendExceptionEmails(createdCare);
            foreach (var unit in units.Values) { var total = createdResidents.Count(r => ((EntityReference)r[SchemaNames.Fields.UnitCensus]).Id == unit.Id); var update = new Entity(SchemaNames.Entities.UnitCensus, unit.Id); update[SchemaNames.Fields.ResidentsTotal] = total; service.Update(update); }
            var censusUpdate = new Entity(SchemaNames.Entities.DailyCensus, dailyId); censusUpdate[SchemaNames.Fields.ResidentsTotal] = createdResidents.Count; service.Update(censusUpdate);
            trace.Trace("Completed {0}: {1} unit censuses, {2} residents, {3} day-of-care.", facilityName, units.Count, createdResidents.Count, createdCare.Count);
        }
        private List<Entity> Query(string name, ColumnSet columns, params FilterExpression[] filters) { var query = new QueryExpression(name) { ColumnSet = columns }; foreach (var filter in filters) query.Criteria.AddFilter(filter); return service.RetrieveMultiple(query).Entities.ToList(); }
        private sealed class FacilityResidentSummary
        {
            internal readonly string FacilityName;
            internal readonly Guid FacilityId;
            internal readonly List<Guid> QueuedFacilityRecordIds = new List<Guid>();
            internal readonly List<string> UnmatchedLivingAreas = new List<string>();
            internal int AlreadyExisted;
            internal int Created;
            internal int Failed;
            internal FacilityResidentSummary(string facilityName, Guid facilityId) { FacilityName = facilityName; FacilityId = facilityId; }
            internal string ToTraceLine()
            {
                var unmatched = UnmatchedLivingAreas.Count == 0 ? "none" : string.Join(", ", UnmatchedLivingAreas);
                return "UCR facility='" + FacilityName + "' (" + FacilityId + "): created=" + Created + "; already-existed=" + AlreadyExisted + "; batch-failed=" + Failed + "; skipped-unmatched-living-area=" + UnmatchedLivingAreas.Count + " [Facility Record / Current Living Area: " + unmatched + "].";
            }
        }
        private static FilterExpression Filter(string field, ConditionOperator op, object value) { var filter = new FilterExpression(LogicalOperator.And); filter.AddCondition(field, op, value); return filter; }
        private static FilterExpression FilterIn(string field, Guid[] values) { var filter = new FilterExpression(LogicalOperator.And); filter.AddCondition(field, ConditionOperator.In, values.Cast<object>().ToArray()); return filter; }
        private static string ReferenceId(EntityReference reference) { return reference == null ? "(blank)" : reference.Id.ToString(); }
        private string PurposeText(Entity absence)
        {
            if (absence == null) return "(none)";
            var value = absence.GetAttributeValue<OptionSetValue>(SchemaNames.Fields.Purpose);
            if (value == null) return "Temporary Absence";
            string label;
            return absence.FormattedValues.TryGetValue(SchemaNames.Fields.Purpose, out label) ? label : "Choice value " + value.Value;
        }
        private string PurposeDetails(Entity absence)
        {
            if (absence == null) return "(none)";
            var value = absence.GetAttributeValue<OptionSetValue>(SchemaNames.Fields.Purpose);
            return value == null ? "Temporary Absence (value blank)" : PurposeText(absence) + " (value " + value.Value + ")";
        }
        private int? FindOptionValue(string entityName, string fieldName, string label)
        {
            var cacheKey = entityName + ":" + fieldName + ":" + NormalizeLabel(label);
            int cachedValue;
            if (optionValueCache.TryGetValue(cacheKey, out cachedValue)) return cachedValue;
            var request = new RetrieveAttributeRequest { EntityLogicalName = entityName, LogicalName = fieldName, RetrieveAsIfPublished = true };
            var response = (RetrieveAttributeResponse)service.Execute(request);
            var attribute = response.AttributeMetadata as EnumAttributeMetadata;
            if (attribute == null) { trace.Trace("Cannot resolve choice '{0}': {1}.{2} is not an option-set field.", label, entityName, fieldName); return null; }
            var matchingOption = attribute.OptionSet.Options.FirstOrDefault(x => x.Value.HasValue && NormalizeLabel(OptionLabel(x)) == NormalizeLabel(label));
            if (matchingOption == null) { trace.Trace("Cannot resolve choice '{0}' on {1}.{2}: no matching option is configured.", label, entityName, fieldName); return null; }
            optionValueCache[cacheKey] = matchingOption.Value.Value;
            return matchingOption.Value.Value;
        }
        private void TraceLookupTargets(string entityName, string fieldName)
        {
            var cacheKey = entityName + ":" + fieldName;
            if (!tracedLookupFields.Add(cacheKey)) return;
            try
            {
                var request = new RetrieveAttributeRequest { EntityLogicalName = entityName, LogicalName = fieldName, RetrieveAsIfPublished = true };
                var response = (RetrieveAttributeResponse)service.Execute(request);
                var attribute = response.AttributeMetadata as LookupAttributeMetadata;
                trace.Trace("Lookup configuration {0}.{1}: Targets={2}.", entityName, fieldName, attribute == null ? "(field is not a lookup)" : string.Join(", ", attribute.Targets));
            }
            catch (Exception ex) { trace.Trace("Could not read lookup configuration for {0}.{1}. Exception: {2}", entityName, fieldName, ex); }
        }
        private void DeactivatePreviousDayOfCareRecords()
        {
            var priorRecords = Query(SchemaNames.Entities.DayOfCare, new ColumnSet(false), Filter(SchemaNames.Fields.Date, ConditionOperator.LessThan, today));
            foreach (var priorRecord in priorRecords)
            {
                var update = new Entity(SchemaNames.Entities.DayOfCare, priorRecord.Id);
                update[SchemaNames.Fields.StateCode] = new OptionSetValue(1);
                service.Update(update);
            }
            trace.Trace("Set statecode=1 (Inactive) on {0} Day of Care records dated before {1:yyyy-MM-dd}.", priorRecords.Count, today);
        }
        private void ActivateTodaysDayOfCareRecords()
        {
            var todaysRecords = Query(SchemaNames.Entities.DayOfCare, new ColumnSet(false), Filter(SchemaNames.Fields.Date, ConditionOperator.On, today));
            foreach (var record in todaysRecords)
            {
                var update = new Entity(SchemaNames.Entities.DayOfCare, record.Id);
                update[SchemaNames.Fields.StateCode] = new OptionSetValue(0);
                service.Update(update);
            }
            trace.Trace("Set statecode=0 (Active) on {0} Day of Care records dated {1:yyyy-MM-dd}.", todaysRecords.Count, today);
        }
        private void ApplyExceptionDetails(Entity care)
        {
            var missing = new List<string>();
            AddIfMissing(care, SchemaNames.Fields.Date, missing);
            AddIfMissing(care, SchemaNames.Fields.Billing, missing);
            AddIfMissing(care, SchemaNames.Fields.FacilityRecord, missing);
            AddIfMissing(care, SchemaNames.Fields.CensusCode, missing);
            AddIfMissing(care, SchemaNames.Fields.DayOfCareBjjsId, missing);
            AddIfMissing(care, SchemaNames.Fields.PlacingCounty, missing);
            AddIfMissing(care, SchemaNames.Fields.Facility, missing);
            AddIfMissing(care, SchemaNames.Fields.LivingArea, missing);
            var hasException = missing.Count > 0;
            care[SchemaNames.Fields.ExceptionStatus] = new OptionSetValue(hasException ? 0 : 1);
            care[SchemaNames.Fields.ExceptionDetails] = hasException ? "Missing required Day of Care fields: " + string.Join(", ", missing) : null;
            if (hasException) trace.Trace("Day of Care for Facility Record {0} has an exception: {1}", ReferenceId(care.GetAttributeValue<EntityReference>(SchemaNames.Fields.FacilityRecord)), string.Join(", ", missing));
        }
        private static void AddIfMissing(Entity entity, string fieldName, List<string> missing)
        {
            object value;
            if (!entity.Attributes.TryGetValue(fieldName, out value) || value == null || (value is string && string.IsNullOrWhiteSpace((string)value))) missing.Add(fieldName);
        }
        private void SendExceptionEmails(IEnumerable<Entity> createdCare)
        {
            var exceptionRecords = createdCare.Where(x =>
            {
                var exceptionStatus = x.GetAttributeValue<OptionSetValue>(SchemaNames.Fields.ExceptionStatus);
                return exceptionStatus != null && exceptionStatus.Value == 0;
            }).ToList();
            if (exceptionRecords.Count == 0) return;
            var recipients = Query("systemuser", new ColumnSet("internalemailaddress", "firstname", "lastname"), Filter("internalemailaddress", ConditionOperator.Equal, "c-gkoukunt@pa.gov"), Filter("firstname", ConditionOperator.Equal, "Goutham Reddy"), Filter("lastname", ConditionOperator.Equal, "Koukuntla"));
            var recipient = recipients.FirstOrDefault();
            if (recipient == null) { trace.Trace("Could not send Day of Care exception email: no active Dataverse user has c-gkoukunt@pa.gov."); return; }
            string organizationUrl = null;
            try { ((RetrieveCurrentOrganizationResponse)service.Execute(new RetrieveCurrentOrganizationRequest())).Detail.Endpoints.TryGetValue(EndpointType.WebApplication, out organizationUrl); }
            catch (Exception ex) { trace.Trace("Could not retrieve the organization URL for Day of Care exception emails: {0}", ex.Message); }
            foreach (var care in exceptionRecords)
            {
                try
                {
                    var recordUrl = string.IsNullOrWhiteSpace(organizationUrl) ? null : organizationUrl.TrimEnd('/') + "/main.aspx?pagetype=entityrecord&etn=" + SchemaNames.Entities.DayOfCare + "&id=" + care.Id;
                    var body = "A Day of Care record was created with an exception.<br/><br/>" +
                        "Name: " + Html(care.GetAttributeValue<string>(SchemaNames.Fields.Name)) + "<br/>" +
                        "Day of Care GUID: " + care.Id + "<br/>" +
                        "Exception: " + Html(care.GetAttributeValue<string>(SchemaNames.Fields.ExceptionDetails)) +
                        (recordUrl == null ? "" : "<br/><br/><a href=\"" + recordUrl + "\">Open the Day of Care record</a>");
                    var email = new Entity("email");
                    email["subject"] = "Day of Care exception: " + (care.GetAttributeValue<string>(SchemaNames.Fields.Name) ?? care.Id.ToString());
                    email["description"] = body;
                    //email["from"] = new[] { new Entity("activityparty") { ["partyid"] = new EntityReference("systemuser", emailSenderId) } };
                    email["from"] = new[] { new Entity("activityparty") { ["partyid"] = recipient.ToEntityReference() } };
                    email["to"] = new[] { new Entity("activityparty") { ["partyid"] = recipient.ToEntityReference() } };
                    var emailId = service.Create(email);
                    service.Execute(new SendEmailRequest { EmailId = emailId, IssueSend = true, TrackingToken = string.Empty });
                    trace.Trace("Sent Day of Care exception email for {0} to c-gkoukunt@pa.gov.", care.Id);
                }
                catch (Exception ex) { trace.Trace("Failed to send Day of Care exception email for {0}: {1}", care.Id, ex); }
            }
        }
        private static string Html(string value) { return System.Security.SecurityElement.Escape(value ?? "") ?? ""; }
        private static string OptionLabel(OptionMetadata option) { return option.Label == null ? "" : option.Label.UserLocalizedLabel == null ? option.Label.LocalizedLabels.Select(x => x.Label).FirstOrDefault() ?? "" : option.Label.UserLocalizedLabel.Label; }
        private static string NormalizeLabel(string label) { return (label ?? "").Replace('\u2013', '-').Replace('\u2014', '-').Trim().ToUpperInvariant(); }
        private List<Entity> CreateBatch(List<Entity> entities, string entityLabel)
        {
            var created = new List<Entity>();
            if (entities.Count == 0) return created;
            var request = new ExecuteMultipleRequest { Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true }, Requests = new OrganizationRequestCollection() };
            foreach (var entity in entities) request.Requests.Add(new CreateRequest { Target = entity });
            var response = (ExecuteMultipleResponse)service.Execute(request);
            var succeeded = 0;
            var failed = 0;
            var notExecuted = 0;
            for (var index = 0; index < request.Requests.Count; index++)
            {
                var batchItem = response.Responses.FirstOrDefault(x => x.RequestIndex == index);
                var target = ((CreateRequest)request.Requests[index]).Target;
                if (batchItem == null)
                {
                    notExecuted++;
                    AddBatchFailure(entityLabel, index, target, "Not executed because an earlier request in the batch failed.");
                    trace.Trace("Did not create {0} at batch index {1}; it was not executed because an earlier request in this batch failed. Fields: {2}", entityLabel, index, DescribeFields(target));
                }
                else if (batchItem.Fault != null)
                {
                    failed++;
                    AddBatchFailure(entityLabel, index, target, "Error " + batchItem.Fault.ErrorCode + ": " + batchItem.Fault.Message);
                    trace.Trace("Failed to create {0} at batch index {1}. Fields: {2}. Error Code={3}; Message={4}; Details={5}", entityLabel, index, DescribeFields(target), batchItem.Fault.ErrorCode, batchItem.Fault.Message, FaultDetails(batchItem.Fault));
                }
                else
                {
                    succeeded++;
                    var createResponse = batchItem.Response as CreateResponse;
                    if (createResponse == null || createResponse.id == Guid.Empty)
                    {
                        failed++;
                        succeeded--;
                        AddBatchFailure(entityLabel, index, target, "Dataverse returned no record ID.");
                        trace.Trace("Failed to create {0} at batch index {1}: Dataverse returned no record ID. Fields: {2}", entityLabel, index, DescribeFields(target));
                        continue;
                    }
                    target.Id = createResponse.id;
                    created.Add(entities[index]);
                }
            }
            trace.Trace("{0} batch completed. Requested={1}; Succeeded={2}; Failed={3}; Not Executed={4}.", entityLabel, entities.Count, succeeded, failed, notExecuted);
            return created;
        }
        private void AddBatchFailure(string entityLabel, int index, Entity target, string reason)
        {
            if (batchFailureSummary.Count >= 25) { omittedBatchFailureCount++; return; }
            var facility = target.GetAttributeValue<EntityReference>(SchemaNames.Fields.Facility);
            var facilityRecord = target.GetAttributeValue<EntityReference>(SchemaNames.Fields.FacilityRecord);
            var compactReason = (reason ?? "Unknown error.").Replace('\r', ' ').Replace('\n', ' ');
            if (compactReason.Length > 500) compactReason = compactReason.Substring(0, 500) + "...";
            batchFailureSummary.Add("Batch failure: entity=" + entityLabel + "; index=" + index + "; facility=" + ReferenceId(facility) + "; facility-record=" + ReferenceId(facilityRecord) + "; reason=" + compactReason);
        }
        private static string DescribeFields(Entity entity) { return string.Join(", ", entity.Attributes.Select(x => x.Key + "=" + (x.Value is EntityReference ? ReferenceId((EntityReference)x.Value) : x.Value is OptionSetValue ? ((OptionSetValue)x.Value).Value.ToString() : x.Value == null ? "(null)" : x.Value.ToString()))); }
        private static string FaultDetails(OrganizationServiceFault fault) { return fault == null ? "" : (string.IsNullOrWhiteSpace(fault.TraceText) ? "" : fault.TraceText) + (fault.InnerFault == null ? "" : " Inner Fault: " + FaultDetails(fault.InnerFault)); }
    }
}
