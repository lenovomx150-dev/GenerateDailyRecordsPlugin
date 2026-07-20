namespace GenerateDailyRecordsPlugin
{
    internal static class SchemaNames
    {
        internal static class Messages { internal const string GenerateDailyRecords = "ucm_GenerateDailyRecords"; }
        internal static class Entities { internal const string Facility = "ucm_admissionprogram"; internal const string FacilityRecord = "ucm_jail"; internal const string Juvenile = "ucm_offender"; internal const string LivingArea = "ucm_program"; internal const string DailyCensus = "ucm_dailycensus"; internal const string UnitCensus = "ucm_unitcensus"; internal const string UnitCensusResident = "ucm_unitcensusresident"; internal const string TemporaryAbsence = "ucm_movements"; internal const string DayOfCare = "ucm_dayofcare"; }
        internal static class Fields
        {
            internal const string Name = "ucm_name", FullName = "ucm_fullname", StateCode = "statecode", Facility = "ucm_facility", FacilityRecordFacility = "ucm_program", CensusDate = "createdon", DailyCensus = "ucm_dailycensus", LivingArea = "ucm_livingarea", UnitCensus = "ucm_unitcensus", FacilityRecord = "ucm_facilityrecord", FacilityRecordJuvenile = "ucm_offendername", UnitCensusResidentJuvenile = "ucm_juvenile", CurrentLivingArea = "ucm_currentlivingarealookup", FacilityRecordMovement = "ucm_jaillookup", Purpose = "ucm_purpose", AbsenceStart = "ucm_datetime", AbsenceEnd = "ucm_returnedtofacility", JuvenileBjjsId = "ucm_juvenileid", DayOfCareBjjsId = "ucm_bjjsid", PlacingCounty = "ucm_placingcounty", Billing = "ucm_billing", Date = "ucm_date", CensusCode = "ucm_reasonfornonbillable", ExceptionStatus = "ucm_exceptionstatus", ExceptionDetails = "ucm_exceptiondetails", ResidentsTotal = "ucm_totalresidents";
            // Confirm these assumed total-column logical names in Dataverse before deploying.
            internal const string AwolTotal = "ucm_awol", CourtTotal = "ucm_court", HospitalTotal = "ucm_hospital", HomePassTotal = "ucm_homepass", OtherTotal = "ucm_other";
        }
    }
}

namespace GenerateDailyRecordsPlugin
{
    internal static class NameHelper
    {
        internal static string Census(string name, System.DateTime date) { return name + " - " + date.ToString("MM/dd/yyyy"); }
    }
}
