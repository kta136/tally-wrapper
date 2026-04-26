using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ShowroomBilling.Contracts.Observability;

namespace ShowroomBilling.Desktop.Services;

public sealed class CorrelationIdHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(CorrelationConstants.HeaderName))
        {
            request.Headers.Add(CorrelationConstants.HeaderName, CorrelationConstants.NewId());
        }

        return base.SendAsync(request, cancellationToken);
    }
}
