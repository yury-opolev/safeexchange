# LocalDev harness (`SAEX_DEV_MODE`)

Local-spike wiring for running the Functions app against a Cosmos DB emulator
instead of Azure. Setting `SAEX_DEV_MODE=true` swaps in `DevCryptoHelper`,
`DevBlobHelper`, `DevDbInitializerHostedService` and the emulator Cosmos
connection in `DevCosmosSetup`. Authentication deliberately stays on the real
middleware, so local dev exercises the same token-validation path production
uses (against the staging Entra app).

**Never set `SAEX_DEV_MODE` in a deployed environment.** Startup refuses a
non-loopback `CosmosDb:CosmosDbEndpoint` in this mode, which contains an
accidental activation — it is not a substitute for the checks below.

## Cosmos emulator

The Linux vNext-preview emulator is Gateway-only; the gateway listens on 8081
and a health endpoint on 8080.

```
docker run --detach --name saex-cosmos \
  --publish 8081:8081 --publish 8080:8080 \
  mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview
```

Wait for `http://localhost:8080/ready` to report `"ready": true`, then put the
emulator account key in user-secrets (it is never hardcoded):

```
dotnet user-secrets set "CosmosDb:PrimaryKey" "<emulator-key>" --project SafeExchange.Functions
```

`local.settings.json` points at `http://localhost:8081`, which is how CI runs
the same emulator for the integration suite. The gateway serves plain HTTP on
loopback: the connection never leaves the machine, and there is no certificate
to validate or to trust.

### If you want the emulator on TLS instead

Starting the container with `-e PROTOCOL=https` switches the gateway to TLS and
makes the endpoint `https://localhost:8081`. The emulator then presents a
certificate that is valid for `localhost` and `127.0.0.1` but is issued by a CA
that only exists inside the image, so your machine has to be told to trust it:

```
docker cp saex-cosmos:/scripts/certs/rootCA.crt .
# Windows (per-user store, no admin rights):
Import-Certificate -FilePath .\rootCA.crt -CertStoreLocation Cert:\CurrentUser\Root
```

Trusting the leaf certificate alone is not enough — it carries an authority key
identifier, so the chain stays partial until the CA is trusted.

Know what that import means before you run it: that CA's private key ships
inside a public container image, so anyone who pulls the image can issue a
certificate that your machine will then trust for **any** host name. Keep it in
the per-user store, and remove it when you are done:

```
Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.Subject -like '*SqlPostgresHostConsole*' } | Remove-Item
```

The plain-HTTP gateway above avoids that trade-off entirely, which is why it is
the default.

## Why there is no certificate-validation bypass

`DevCosmosSetup` configures the Cosmos client with transport settings only, and
installs no `HttpClientFactory`. It used to install one carrying
`HttpClientHandler.DangerousAcceptAnyServerCertificateValidator` so the
emulator's self-signed certificate would be accepted. That callback is
unconditional: it accepted an untrusted certificate, a hostname mismatch, or any
other validation error, from whichever endpoint the configuration named — the
connection had no server authentication at all.

If you previously ran the emulator over TLS and relied on that bypass, the
migration is one of the two options above: move to the plain-HTTP gateway, or
trust the emulator certificate.

TLS failures now surface as TLS failures, with no insecure handler to fall back
to. `SafeExchange.Tests/Tests/DevModeCosmosTlsTests.cs` holds that line: it
builds the Cosmos options from the real startup code and asserts the resulting
client rejects an untrusted certificate and a certificate that does not match
the host.

The same applies to the integration suite's `ConnectionStrings:CosmosDb`
user-secret: `DisableServerCertificateValidation=True` in a Cosmos connection
string is the same bypass by another route. CI sets a plain-HTTP endpoint with
no such flag.
