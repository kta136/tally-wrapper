using System.Xml.Linq;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Tests.TestSupport;

internal sealed class FakeXmlClient(XElement response) : ITallyXmlClient
{
    public XElement? LastRequest { get; private set; }

    public Task<XElement> SendAsync(XElement request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(response);
    }

    public Task<XElement> SendWriteAsync(XElement request, CancellationToken cancellationToken = default) =>
        SendAsync(request, cancellationToken);
}
