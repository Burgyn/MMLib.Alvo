#load "../_shared/Rows.csx"

using System.Security.Cryptography;
using System.Text;

// The technician app signs its delivery the way every Standard Webhooks sender does: HMAC-SHA256 over
// `id.timestamp.body` with the base64 key after `whsec_`. The init script runs BEFORE the requests, so it builds the
// exact compact body the request sends ({{MobileBody}}, on one line) and signs those bytes — a body reformatted by
// anything in between would no longer verify, which is the receiver's whole point.

string Sign(string id, string timestamp, string body)
{
    var secret = tp.GetVariable<string>("techAppSecret");
    var key = Convert.FromBase64String(secret.Substring("whsec_".Length));
    return "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));
}

var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
var body = $"{{\"workOrderId\":\"{Constant("Wo7001Id")}\",\"tenantId\":\"{Constant("tenantNorth")}\",\"completedOn\":\"2027-03-01\",\"minutesOnSite\":95}}";

var id = $"msg_{Guid.NewGuid():N}";
tp.SetVariable("MobileId", id);
tp.SetVariable("MobileTimestamp", timestamp);
tp.SetVariable("MobileBody", body);
tp.SetVariable("MobileSignature", Sign(id, timestamp, body));

// A different id, and a signature made with a key that is not the app's: the receiver must refuse it before any code runs.
tp.SetVariable("ForgedId", $"msg_{Guid.NewGuid():N}");
tp.SetVariable("ForgedSignature", "v1," + Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("not-the-technician-app-key-at-all"), Encoding.UTF8.GetBytes(body))));
