using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace GenerateDailyRecordsPlugin
{
    internal sealed class DailyRecordService
    {
        private readonly IOrganizationService service;
        private readonly ITracingService trace;
        private readonly DateTime today;
        internal DailyRecordService(IOrganizationService service, ITracingService trace, DateTime today) { this.service = service; this.trace = trace; this.today = today.Date; }
        internal void Generate()
        {
            var facilities = Query(SchemaNames.Entities.Facility, new ColumnSet(SchemaNames.Fields.Name));
            trace.Trace("Found {0} facilities to process.", facilities.Count);
            foreach (var facility in facilities)
            {
                try { GenerateFacility(facility); }
                catch (Exception ex) { trace.Trace("Facility {0} failed. Exception: {1}", facility.Id, ex); }
            }
        }
        private void GenerateFacility(Entity facility)
        {
            var facilityName = facility.GetAttributeValue<string>(SchemaNames.Fields.Name) ?? facility.Id.ToString();
            trace.Trace("Processing facility '{0}' ({1}) for {2:yyyy-MM-dd}.", facilityName, facility.Id, today);
            var existing = Query(SchemaNames.Entities.DailyCensus, new ColumnSet(false), Filter(SchemaNames.Fields.Facility, ConditionOperator.Equal, facility.Id), Filter(SchemaNames.Fields.CensusDate, ConditionOperator.On, today)).FirstOrDefault();
            if (existing != null) { trace.Trace("Skipped {0}: census already exists.", facility.Id); return; }
            var census = new Entity(SchemaNames.Entities.DailyCensus); census[SchemaNames.Fields.Name] = NameHelper.Census(facilityName, today); census[SchemaNames.Fields.Facility] = facility.ToEntityReference();
            var dailyId = service.Create(census); var daily = new EntityReference(SchemaNames.Entities.DailyCensus, dailyId);
            trace.Trace("Created Daily Census {0} for facility '{1}'.", dailyId, facilityName);
            var areas = Query(SchemaNames.Entities.LivingArea, new ColumnSet(SchemaNames.Fields.Name), Filter(SchemaNames.Fields.Facility, ConditionOperator.Equal, facility.Id));
            trace.Trace("Found {0} Living Areas for facility '{1}'.", areas.Count, facilityName);
            var units = new Dictionary<Guid, EntityReference>();
            foreach (var area in areas)
            {
                var unit = new Entity(SchemaNames.Entities.UnitCensus); unit[SchemaNames.Fields.Name] = NameHelper.Census(area.GetAttributeValue<string>(SchemaNames.Fields.Name) ?? area.Id.ToString(), today); unit[SchemaNames.Fields.DailyCensus] = daily; unit[SchemaNames.Fields.Facility] = facility.ToEntityReference(); unit[SchemaNames.Fields.LivingArea] = area.ToEntityReference();
                var unitId = service.Create(unit);
                units.Add(area.Id, new EntityReference(SchemaNames.Entities.UnitCensus, unitId));
                trace.Trace("Created Unit Census {0} for Living Area '{1}' ({2}).", unitId, area.GetAttributeValue<string>(SchemaNames.Fields.Name) ?? "(no name)", area.Id);
            }
            var records = Query(SchemaNames.Entities.FacilityRecord, new ColumnSet(SchemaNames.Fields.CurrentLivingArea, SchemaNames.Fields.Juvenile, SchemaNames.Fields.PlacingCounty), Filter(SchemaNames.Fields.FacilityRecordFacility, ConditionOperator.Equal, facility.Id), Filter(SchemaNames.Fields.StateCode, ConditionOperator.Equal, 0));
            trace.Trace("Found {0} active Facility Records for facility '{1}'.", records.Count, facilityName);
            var existingResidents = Query(SchemaNames.Entities.UnitCensusResident, new ColumnSet(SchemaNames.Fields.FacilityRecord), Filter(SchemaNames.Fields.CensusDate, ConditionOperator.On, today));
            var existingResidentRecordIds = new HashSet<Guid>(existingResidents.Where(x => x.Contains(SchemaNames.Fields.FacilityRecord)).Select(x => x.GetAttributeValue<EntityReference>(SchemaNames.Fields.FacilityRecord).Id));
            var existingCare = Query(SchemaNames.Entities.DayOfCare, new ColumnSet(SchemaNames.Fields.FacilityRecord), Filter(SchemaNames.Fields.Date, ConditionOperator.On, today));
            var existingCareRecordIds = new HashSet<Guid>(existingCare.Where(x => x.Contains(SchemaNames.Fields.FacilityRecord)).Select(x => x.GetAttributeValue<EntityReference>(SchemaNames.Fields.FacilityRecord).Id));
            trace.Trace("Duplicate protection found {0} Unit Census Residents and {1} Day of Care records already created today.", existingResidentRecordIds.Count, existingCareRecordIds.Count);
            var juvenileIds = records.Where(r => r.Contains(SchemaNames.Fields.Juvenile)).Select(r => r.GetAttributeValue<EntityReference>(SchemaNames.Fields.Juvenile).Id).Distinct().ToArray();
            var juveniles = juvenileIds.Length == 0 ? new List<Entity>() : Query(SchemaNames.Entities.Juvenile, new ColumnSet(SchemaNames.Fields.BjjsId), FilterIn("ucm_offenderid", juvenileIds));
            var bjjsByJuvenile = juveniles.ToDictionary(j => j.Id, j => j.GetAttributeValue<string>(SchemaNames.Fields.BjjsId));
            var absences = Query(SchemaNames.Entities.TemporaryAbsence, new ColumnSet(SchemaNames.Fields.FacilityRecordMovement, SchemaNames.Fields.Purpose, SchemaNames.Fields.AbsenceStart, SchemaNames.Fields.AbsenceEnd), Filter(SchemaNames.Fields.StateCode, ConditionOperator.Equal, 0), Filter(SchemaNames.Fields.AbsenceStart, ConditionOperator.OnOrBefore, today));
            var absenceByRecord = absences.Where(x => x.Contains(SchemaNames.Fields.FacilityRecordMovement) && (!x.Contains(SchemaNames.Fields.AbsenceEnd) || x.GetAttributeValue<DateTime>(SchemaNames.Fields.AbsenceEnd).Date >= today)).GroupBy(x => x.GetAttributeValue<EntityReference>(SchemaNames.Fields.FacilityRecordMovement).Id).ToDictionary(x => x.Key, x => x.First());
            trace.Trace("Found {0} active Temporary Absences applicable today across all facilities; {1} are linked to Facility Records.", absences.Count, absenceByRecord.Count);
            var residentCreates = new List<Entity>(); var careCreates = new List<Entity>();
            foreach (var record in records)
            {
                Entity absence; absenceByRecord.TryGetValue(record.Id, out absence); var area = record.GetAttributeValue<EntityReference>(SchemaNames.Fields.CurrentLivingArea); EntityReference unit;
                var juvenileReference = record.GetAttributeValue<EntityReference>(SchemaNames.Fields.Juvenile);
                trace.Trace("Facility Record {0}: Juvenile={1}; Current Living Area={2}; Active Absence={3}; Purpose={4}.", record.Id, ReferenceId(juvenileReference), ReferenceId(area), absence == null ? "No" : "Yes", PurposeDetails(absence));
                if (area != null && units.TryGetValue(area.Id, out unit))
                {
                    if (existingResidentRecordIds.Contains(record.Id))
                    {
                        trace.Trace("Skipped Unit Census Resident for Facility Record {0}: a resident record already exists today.", record.Id);
                    }
                    else
                    {
                        var resident = new Entity(SchemaNames.Entities.UnitCensusResident); resident[SchemaNames.Fields.Name] = record.Id.ToString(); resident[SchemaNames.Fields.UnitCensus] = unit; resident[SchemaNames.Fields.DailyCensus] = daily; resident[SchemaNames.Fields.FacilityRecord] = record.ToEntityReference(); resident[SchemaNames.Fields.Facility] = facility.ToEntityReference(); if (record.Contains(SchemaNames.Fields.Juvenile)) resident[SchemaNames.Fields.Juvenile] = record[SchemaNames.Fields.Juvenile]; if (absence != null && absence.Contains(SchemaNames.Fields.Purpose)) resident[SchemaNames.Fields.Purpose] = absence[SchemaNames.Fields.Purpose]; residentCreates.Add(resident);
                        trace.Trace("Queued Unit Census Resident for Facility Record {0}. Unit Census={1}; Purpose={2}.", record.Id, unit.Id, PurposeDetails(absence));
                    }
                }
                else
                {
                    trace.Trace("Did not queue Unit Census Resident for Facility Record {0}. Current Living Area is {1}.", record.Id, area == null ? "blank" : "not one of this facility's Living Areas (" + area.Id + ")");
                }
                var daysAway = absence == null ? 0 : (today - absence.GetAttributeValue<DateTime>(SchemaNames.Fields.AbsenceStart).Date).Days + 1; if (daysAway >= 7) continue;
                if (existingCareRecordIds.Contains(record.Id)) { trace.Trace("Skipped Day of Care for Facility Record {0}: a Day of Care record already exists for {1:yyyy-MM-dd}.", record.Id, today); continue; }
                var juvenile = juvenileReference; string bjjsId = null; if (juvenile != null) bjjsByJuvenile.TryGetValue(juvenile.Id, out bjjsId); var care = new Entity(SchemaNames.Entities.DayOfCare); care[SchemaNames.Fields.Name] = NameHelper.Census(string.IsNullOrWhiteSpace(bjjsId) ? record.Id.ToString() : bjjsId, today); care[SchemaNames.Fields.FacilityRecord] = record.ToEntityReference(); care[SchemaNames.Fields.Date] = today; care[SchemaNames.Fields.Billing] = new OptionSetValue(daysAway == 6 ? 0 : 1); care[SchemaNames.Fields.CensusCode] = absence == null ? "In Care" : PurposeText(absence); if (juvenile != null) { care[SchemaNames.Fields.Juvenile] = juvenile; care["ucm_bjjsid"] = bjjsId; } if (record.Contains(SchemaNames.Fields.PlacingCounty)) care[SchemaNames.Fields.PlacingCounty] = record[SchemaNames.Fields.PlacingCounty]; careCreates.Add(care);
                trace.Trace("Queued Day of Care for Facility Record {0}. Days Away={1}; Billing={2}; Census Code='{3}'.", record.Id, daysAway, daysAway == 6 ? "Non-billable (0)" : "Billable (1)", absence == null ? "In Care" : PurposeText(absence));
            }
            CreateBatch(residentCreates, "Unit Census Resident"); CreateBatch(careCreates, "Day of Care"); foreach (var unit in units.Values) { var total = residentCreates.Count(r => ((EntityReference)r[SchemaNames.Fields.UnitCensus]).Id == unit.Id); var update = new Entity(SchemaNames.Entities.UnitCensus, unit.Id); update[SchemaNames.Fields.ResidentsTotal] = total; service.Update(update); trace.Trace("Updated Unit Census {0}: Total Residents={1}.", unit.Id, total); } var censusUpdate = new Entity(SchemaNames.Entities.DailyCensus, dailyId); censusUpdate[SchemaNames.Fields.ResidentsTotal] = residentCreates.Count; service.Update(censusUpdate);
            trace.Trace("Completed {0}: {1} unit censuses, {2} residents, {3} day-of-care.", facilityName, units.Count, residentCreates.Count, careCreates.Count);
        }
        private List<Entity> Query(string name, ColumnSet columns, params FilterExpression[] filters) { var query = new QueryExpression(name) { ColumnSet = columns }; foreach (var filter in filters) query.Criteria.AddFilter(filter); return service.RetrieveMultiple(query).Entities.ToList(); }
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
        private void CreateBatch(List<Entity> entities, string entityLabel)
        {
            if (entities.Count == 0) { trace.Trace("No {0} records to create.", entityLabel); return; }
            var request = new ExecuteMultipleRequest { Settings = new ExecuteMultipleSettings { ContinueOnError = false, ReturnResponses = true }, Requests = new OrganizationRequestCollection() };
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
                    trace.Trace("Did not create {0} at batch index {1}; it was not executed because an earlier request in this batch failed. Fields: {2}", entityLabel, index, DescribeFields(target));
                }
                else if (batchItem.Fault != null)
                {
                    failed++;
                    trace.Trace("Failed to create {0} at batch index {1}. Fields: {2}. Error Code={3}; Message={4}; Details={5}", entityLabel, index, DescribeFields(target), batchItem.Fault.ErrorCode, batchItem.Fault.Message, FaultDetails(batchItem.Fault));
                }
                else
                {
                    succeeded++;
                }
            }
            trace.Trace("{0} batch completed. Requested={1}; Succeeded={2}; Failed={3}; Not Executed={4}.", entityLabel, entities.Count, succeeded, failed, notExecuted);
        }
        private static string DescribeFields(Entity entity) { return string.Join(", ", entity.Attributes.Select(x => x.Key + "=" + (x.Value is EntityReference ? ReferenceId((EntityReference)x.Value) : x.Value is OptionSetValue ? ((OptionSetValue)x.Value).Value.ToString() : x.Value == null ? "(null)" : x.Value.ToString()))); }
        private static string FaultDetails(OrganizationServiceFault fault) { return fault == null ? "" : (string.IsNullOrWhiteSpace(fault.TraceText) ? "" : fault.TraceText) + (fault.InnerFault == null ? "" : " Inner Fault: " + FaultDetails(fault.InnerFault)); }
    }
}
