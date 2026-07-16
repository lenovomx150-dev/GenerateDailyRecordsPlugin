using System;
using Microsoft.Xrm.Sdk;

namespace GenerateDailyRecordsPlugin
{
    /// <summary>Handler for the unbound ucm_GenerateDailyRecords Custom API.</summary>
    public sealed class GenerateDailyRecordsPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            if (tracing == null || context == null || factory == null)
                throw new InvalidPluginExecutionException("Dataverse plugin services could not be initialized.");

            if (!string.Equals(context.MessageName, SchemaNames.Messages.GenerateDailyRecords, StringComparison.OrdinalIgnoreCase))
                return;

            var service = factory.CreateOrganizationService(context.UserId);
            tracing.Trace("ucm_GenerateDailyRecords started. Correlation: {0}", context.CorrelationId);
            try
            {
                new DailyRecordService(service, tracing, DateTime.UtcNow.Date).Generate();
                tracing.Trace("ucm_GenerateDailyRecords completed.");
            }
            catch (Exception ex)
            {
                tracing.Trace("ucm_GenerateDailyRecords failed. Correlation: {0}. Exception: {1}", context.CorrelationId, ex);
                throw;
            }
        }
    }
}
