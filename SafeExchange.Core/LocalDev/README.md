# LocalDev harness (`SAEX_DEV_MODE`)

Local-spike wiring for running the Functions app against a Cosmos DB emulator
instead of Azure. Setting `SAEX_DEV_MODE=true` swaps in `DevCryptoHelper`,
`DevBlobHelper`, `DevDbInitializerHostedService` and the emulator Cosmos
connection in `DevCosmosSetup`. Authentication deliberately stays on the real
middleware, so local dev exercises the same token-validation path production
uses (against the staging Entra app).

**Never set `SAEX_DEV_MODE` in a deployed environment.** `DevCosmosSetup`
rejects a non-loopback `CosmosDb:CosmosDbEndpoint` in this mode, so the harness
enforces its own local-only constraint rather than relying on this document.
That is containment for an accidental activation — it is not server
authentication, which is what the certificate validation below provides.

Use test-only data here. The emulator account key is a well-known public value,
so never point this harness at real credentials or user data.

## Emulator over HTTPS with your own certificate

The supported setup. You generate the certificate, so its private key is yours;
nothing here trusts a key that other people also have.

### 1. Create the certificate

End-entity certificate, valid for the loopback names only:

```powershell
$cert = New-SelfSignedCertificate `
  -Subject "CN=SafeExchange local Cosmos emulator" `
  -TextExtension @("2.5.29.17={text}DNS=localhost&IPAddress=127.0.0.1", "2.5.29.37={text}1.3.6.1.5.5.7.3.1") `
  -KeyUsage DigitalSignature, KeyEncipherment `
  -KeyAlgorithm RSA -KeyLength 2048 `
  -CertStoreLocation Cert:\CurrentUser\My `
  -NotAfter (Get-Date).AddYears(1)

$cert.Thumbprint    # keep this - trust and cleanup below are keyed on it

$password = Read-Host -AsSecureString -Prompt "PFX password"
Export-PfxCertificate -Cert $cert -FilePath .\emulator.pfx -Password $password
Export-Certificate   -Cert $cert -FilePath .\emulator.cer
```

### 2. Trust it, by thumbprint

```powershell
Import-Certificate -FilePath .\emulator.cer -CertStoreLocation Cert:\CurrentUser\Root
```

This is an end-entity certificate with no CA capability, so trusting it
authorises exactly that one certificate, for `localhost` and `127.0.0.1` only.
It cannot sign a certificate for any other host, and the trust is per-user.

### 3. Run the emulator on it

```powershell
docker run --detach --name saex-cosmos `
  --publish 127.0.0.1:8081:8081 --publish 127.0.0.1:8080:8080 `
  --mount "type=bind,source=$PWD\emulator.pfx,target=/emulator.pfx,readonly" `
  -e PROTOCOL=https -e CERT_PATH=/emulator.pfx -e CERT_SECRET="<the PFX password>" `
  mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview
```

Publishing on `127.0.0.1:` matters: `--publish 8081:8081` binds every interface
and exposes the emulator to your network.

Wait for `http://localhost:8080/ready` to report `"ready": true`, then put the
emulator account key in user-secrets (it is never hardcoded):

```
dotnet user-secrets set "CosmosDb:PrimaryKey" "<emulator-key>" --project SafeExchange.Functions
```

`local.settings.json` already points at `https://localhost:8081`.

### 4. Replace it when it expires, and clean up

The certificate above lasts a year. When it expires, or when you are done with
the harness, remove exactly what you installed — matched on the thumbprint, not
on a subject name that could match something else:

```powershell
$thumbprint = "<the thumbprint from step 1>"
Get-ChildItem Cert:\CurrentUser\Root, Cert:\CurrentUser\My |
  Where-Object Thumbprint -eq $thumbprint | Remove-Item
```

Then repeat from step 1 if you still need the emulator.

### Not the emulator's built-in certificate

Running the emulator without `CERT_PATH` makes it serve a certificate issued by
a CA baked into the public container image. Trusting that CA would put a signing
key that anyone can pull from the image into your trusted roots, where it could
authenticate **any** host name — far beyond this connection, and for as long as
it stayed installed. Generate your own certificate instead.

## Alternative: loopback HTTP harness

If you would rather not manage a certificate, the emulator's gateway also serves
plain HTTP, which is how CI runs it for the integration suite:

```powershell
docker run --detach --name saex-cosmos `
  --publish 127.0.0.1:8081:8081 --publish 127.0.0.1:8080:8080 `
  mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview
```

Set `CosmosDb:CosmosDbEndpoint` to `http://localhost:8081`. There is no
certificate, and nothing pretends there is one: the connection is unencrypted
and unauthenticated, and it is acceptable only because it never leaves the
machine. Publish on `127.0.0.1:` as above, and keep to test-only keys and data.

Loopback is not isolation from other local processes. Any process on the machine
can reach the emulator, and can bind the port if the emulator is not running, so
this is a weaker arrangement than the certificate setup above — use it
knowingly. It changes nothing about the deployed application, which keeps its
HTTPS, credential-based Cosmos connection.

## Why there is no certificate-validation bypass

`DevCosmosSetup` configures the Cosmos client with transport settings only, and
installs no `HttpClientFactory`. It used to install one carrying
`HttpClientHandler.DangerousAcceptAnyServerCertificateValidator` so the
emulator's self-signed certificate would be accepted. That callback is
unconditional: it accepted an untrusted certificate, a hostname mismatch, or any
other validation error, from whichever endpoint the configuration named — the
connection had no server authentication at all.

TLS failures now surface as TLS failures, with no insecure handler to fall back
to. `SafeExchange.Tests/Tests/CosmosCertificateValidationTests.cs` holds that
line for both modes: it builds the Cosmos options from the real startup code and
asserts the resulting client rejects an untrusted certificate and one that does
not match the host, in development mode and on the production credential path.

The same applies to the integration suite's `ConnectionStrings:CosmosDb`
user-secret: `DisableServerCertificateValidation=True` in a Cosmos connection
string is the same bypass by another route. Point that connection string at a
loopback HTTP emulator, or at an HTTPS emulator using the certificate setup
above, rather than disabling validation.
