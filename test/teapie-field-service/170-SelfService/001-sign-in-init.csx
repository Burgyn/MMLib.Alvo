#load "../_shared/TestIssuer.csx"

// Three people per run (D10), minted HERE — the init of the case that exchanges them — so no ID token is older
// than a few seconds when Alvo judges it (an `iat` more than five minutes old is refused). Each subject is a role
// word plus a fresh GUID and each address is unique to the run, so 170-SelfService runs alone and runs again on a
// live stack: every person it signs in is new to the database it finds.
//
//   Dana — the dispatcher who will found Acme Repairs.
//   Theo — the technician she will invite.
//   Olga — an outsider, who belongs to no tenant of theirs.

var run = Guid.NewGuid().ToString("N").Substring(0, 8);

void Person(string variable, string role, string firstName, string fullName)
{
    var email = $"{firstName}.{run}@gmail.com";
    tp.SetVariable($"{variable}Email", email);
    tp.SetVariable($"{variable}Name", fullName);
    tp.SetVariable($"{variable}IdToken", IdToken($"{role}-{Guid.NewGuid():N}", email, fullName));
}

Person("Dana", "dispatcher", "dana", "Dana Dispatcher");
Person("Theo", "technician", "theo", "Theo Technician");
Person("Olga", "outsider", "olga", "Olga Outsider");

// Olga's claims, signed by a key the issuer never published but naming the published key's `kid`: only the
// signature can tell this token from her real one.
tp.SetVariable("OlgaForgedIdToken", IdTokenSignedByAnotherKey($"outsider-{Guid.NewGuid():N}", tp.GetVariable<string>("OlgaEmail"), "Olga Outsider"));

// The run's own region, so nothing here leans on 010-Seed's rows: `code` is unique and at most 12 characters.
tp.SetVariable("RegionCode", "SS" + run.ToUpperInvariant());

// The key set the sink must serve before the first sign-in — the request file's first request posts it.
tp.SetVariable("IssuerKeySetImport", TestIssuerKeySetImport());
