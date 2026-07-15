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
            foreach (var facility in facilities) { try { GenerateFacility(facility); } catch (Exception ex) { trace.Trace("Facility {0} failed: {1}", facility.Id, ex); } }
        }
        private void GenerateFacility(Entity facility)
        {
            var existing = Query(SchemaNames.Entities.DailyCensus, new ColumnSet(false), Filter(SchemaNames.Fields.Facility, ConditionOperator.Equal, facility.Id), Filter(SchemaNames.Fields.CensusDate, ConditionOperator.On, today)).FirstOrDefault();
            if (existing != null) { trace.Trace("Skipped {0}: census already exists.", facility.Id); return; }
            var facilityName = facility.GetAttributeValue<string>(SchemaNames.Fields.Name) ?? facility.Id.ToString();
            var census = new Entity(SchemaNames.Entities.DailyCensus); census[SchemaNames.Fields.Name] = NameHelper.Census(facilityName, today); census[SchemaNames.Fields.Facility] = facility.ToEntityReference();
            var dailyId = service.Create(census); var daily = new EntityReference(SchemaNames.Entities.DailyCensus, dailyId);
            var areas = Query(SchemaNames.Entities.LivingArea, new ColumnSet(SchemaNames.Fields.Name), Filter(SchemaNames.Fields.Facility, ConditionOperator.Equal, facility.Id));
            var units = new Dictionary<Guid, EntityReference>();
            foreach (var area in areas)
            {
                var unit = new Entity(SchemaNames.Entities.UnitCensus); unit[SchemaNames.Fields.Name] = NameHelper.Census(area.GetAttributeValue<string>(SchemaNames.Fields.Name) ?? area.Id.ToString(), today); unit[SchemaNames.Fields.DailyCensus] = daily; unit[SchemaNames.Fields.Facility] = facility.ToEntityReference(); unit[SchemaNames.Fields.LivingArea] = area.ToEntityReference();
                units.Add(area.Id, new EntityReference(SchemaNames.Entities.UnitCensus, service.Create(unit)));
            }
            var records = Query(SchemaNames.Entities.FacilityRecord, new ColumnSet(SchemaNames.Fields.CurrentLivingArea, SchemaNames.Fields.Juvenile, SchemaNames.Fields.PlacingCounty), Filter(SchemaNames.Fields.FacilityRecordFacility, ConditionOperator.Equal, facility.Id), Filter(SchemaNames.Fields.StateCode, ConditionOperator.Equal, 0));
            var juvenileIds = records.Where(r => r.Contains(SchemaNames.Fields.Juvenile)).Select(r => r.GetAttributeValue<EntityReference>(SchemaNames.Fields.Juvenile).Id).Distinct().ToArray();
            var juveniles = juvenileIds.Length == 0 ? new List<Entity>() : Query(SchemaNames.Entities.Juvenile, new ColumnSet(SchemaNames.Fields.BjjsId), FilterIn("ucm_offenderid", juvenileIds));
            var bjjsByJuvenile = juveniles.ToDictionary(j => j.Id, j => j.GetAttributeValue<string>(SchemaNames.Fields.BjjsId));
            var absences = Query(SchemaNames.Entities.TemporaryAbsence, new ColumnSet(SchemaNames.Fields.FacilityRecordMovement, SchemaNames.Fields.Purpose, SchemaNames.Fields.AbsenceStart, SchemaNames.Fields.AbsenceEnd), Filter(SchemaNames.Fields.StateCode, ConditionOperator.Equal, 0), Filter(SchemaNames.Fields.AbsenceStart, ConditionOperator.OnOrBefore, today));
            var absenceByRecord = absences.Where(x => x.Contains(SchemaNames.Fields.FacilityRecordMovement) && (!x.Contains(SchemaNames.Fields.AbsenceEnd) || x.GetAttributeValue<DateTime>(SchemaNames.Fields.AbsenceEnd).Date >= today)).GroupBy(x => x.GetAttributeValue<EntityReference>(SchemaNames.Fields.FacilityRecordMovement).Id).ToDictionary(x => x.Key, x => x.First());
            var residentCreates = new List<Entity>(); var careCreates = new List<Entity>();
            foreach (var record in records)
            {
                Entity absence; absenceByRecord.TryGetValue(record.Id, out absence); var area = record.GetAttributeValue<EntityReference>(SchemaNames.Fields.CurrentLivingArea); EntityReference unit;
                if (area != null && units.TryGetValue(area.Id, out unit)) { var resident = new Entity(SchemaNames.Entities.UnitCensusResident); resident[SchemaNames.Fields.Name] = record.Id.ToString(); resident[SchemaNames.Fields.UnitCensus] = unit; resident[SchemaNames.Fields.DailyCensus] = daily; resident[SchemaNames.Fields.FacilityRecord] = record.ToEntityReference(); resident[SchemaNames.Fields.Facility] = facility.ToEntityReference(); if (record.Contains(SchemaNames.Fields.Juvenile)) resident[SchemaNames.Fields.Juvenile] = record[SchemaNames.Fields.Juvenile]; if (absence != null && absence.Contains(SchemaNames.Fields.Purpose)) resident[SchemaNames.Fields.Purpose] = absence[SchemaNames.Fields.Purpose]; residentCreates.Add(resident); }
                var daysAway = absence == null ? 0 : (today - absence.GetAttributeValue<DateTime>(SchemaNames.Fields.AbsenceStart).Date).Days + 1; if (daysAway >= 7) continue;
                var juvenile = record.GetAttributeValue<EntityReference>(SchemaNames.Fields.Juvenile); string bjjsId = null; if (juvenile != null) bjjsByJuvenile.TryGetValue(juvenile.Id, out bjjsId); var care = new Entity(SchemaNames.Entities.DayOfCare); care[SchemaNames.Fields.Name] = NameHelper.Census(string.IsNullOrWhiteSpace(bjjsId) ? record.Id.ToString() : bjjsId, today); care[SchemaNames.Fields.FacilityRecord] = record.ToEntityReference(); care[SchemaNames.Fields.Date] = today; care[SchemaNames.Fields.Billing] = new OptionSetValue(daysAway == 6 ? 0 : 1); care[SchemaNames.Fields.CensusCode] = absence == null ? "In Care" : PurposeText(absence); if (juvenile != null) { care[SchemaNames.Fields.Juvenile] = juvenile; care["ucm_bjjsid"] = bjjsId; } if (record.Contains(SchemaNames.Fields.PlacingCounty)) care[SchemaNames.Fields.PlacingCounty] = record[SchemaNames.Fields.PlacingCounty]; careCreates.Add(care);
            }
            CreateBatch(residentCreates); CreateBatch(careCreates); foreach (var unit in units.Values) { var update = new Entity(SchemaNames.Entities.UnitCensus, unit.Id); update[SchemaNames.Fields.ResidentsTotal] = residentCreates.Count(r => ((EntityReference)r[SchemaNames.Fields.UnitCensus]).Id == unit.Id); service.Update(update); } var censusUpdate = new Entity(SchemaNames.Entities.DailyCensus, dailyId); censusUpdate[SchemaNames.Fields.ResidentsTotal] = residentCreates.Count; service.Update(censusUpdate);
            trace.Trace("Completed {0}: {1} unit censuses, {2} residents, {3} day-of-care.", facilityName, units.Count, residentCreates.Count, careCreates.Count);
        }
        private List<Entity> Query(string name, ColumnSet columns, params FilterExpression[] filters) { var query = new QueryExpression(name) { ColumnSet = columns }; foreach (var filter in filters) query.Criteria.AddFilter(filter); return service.RetrieveMultiple(query).Entities.ToList(); }
        private static FilterExpression Filter(string field, ConditionOperator op, object value) { var filter = new FilterExpression(LogicalOperator.And); filter.AddCondition(field, op, value); return filter; }
        private static FilterExpression FilterIn(string field, Guid[] values) { var filter = new FilterExpression(LogicalOperator.And); filter.AddCondition(field, ConditionOperator.In, values.Cast<object>().ToArray()); return filter; }
        private string PurposeText(Entity absence) { var value = absence.GetAttributeValue<OptionSetValue>(SchemaNames.Fields.Purpose); return value == null ? "Temporary Absence" : value.Value.ToString(); }
        private void CreateBatch(List<Entity> entities) { var request = new ExecuteMultipleRequest { Settings = new ExecuteMultipleSettings { ContinueOnError = false, ReturnResponses = false }, Requests = new OrganizationRequestCollection() }; foreach (var entity in entities) request.Requests.Add(new CreateRequest { Target = entity }); if (request.Requests.Count > 0) service.Execute(request); }
    }
}
