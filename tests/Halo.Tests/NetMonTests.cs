using Halo.ClaudeCode;
using Halo.Codex;

namespace Halo.Tests;

// A 529 Overloaded from Anthropic (or the OpenAI equivalent) has to register as "down" — this pins the
// status-code mapping both NetMons use to decide that, extracted out of the live HTTP probe so it is
// testable without a network call.
public class NetMonTests
{
    [Theory]
    [InlineData(529, true)]   // the actual overload status this exists for
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(403, true)]   // Cloudflare/WAF geoblock
    [InlineData(407, true)]   // proxy auth
    [InlineData(429, true)]   // rate-limited
    [InlineData(200, false)]
    [InlineData(405, false)]  // the healthy answer to a GET on a POST-only route
    [InlineData(404, false)]  // reachable, just the wrong path
    [InlineData(401, false)]  // reachable, just unauthenticated
    [InlineData(499, false)]  // boundary: one below the 5xx cutoff
    public void ClaudeCode_maps_status_to_down(int status, bool expectDown)
        => Assert.Equal(expectDown, NetMon.IsDownStatus(status));

    [Theory]
    [InlineData(529, true)]
    [InlineData(500, true)]
    [InlineData(403, true)]
    [InlineData(407, true)]
    [InlineData(429, true)]
    [InlineData(200, false)]
    [InlineData(404, false)]
    [InlineData(401, false)]
    public void Codex_maps_status_to_down(int status, bool expectDown)
        => Assert.Equal(expectDown, CodexNetMon.IsDownStatus(status));
}
