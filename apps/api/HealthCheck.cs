
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiAgentStudio.Api
{
    public class HealthCheck : IHealthCheck
    {
        string Fab {get; set;}
        string Shop {get; set;}
        
        public HealthCheck() 
        {

        }

        public HealthCheck(string fab, string shop)
        {
            this.Fab = fab;
            this.Shop = shop;
        }
        
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var healthCheckResultHealthy = true;
            // check entityframework
            // using (var shopContext = new UacContext(Shop)) {
            //     if (!shopContext.Database.CanConnect())
            //     {
            //         healthCheckResultHealthy = false;
            //     }
            // }

            if (healthCheckResultHealthy)
            {
                return Task.FromResult(
                    HealthCheckResult.Healthy("A healthy result."));
            }

            return Task.FromResult(
                new HealthCheckResult(context.Registration.FailureStatus,
                "An unhealthy result."));
        }
    }
}