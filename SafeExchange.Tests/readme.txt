
# how to run unit tests locally:

# 1. install Cosmos Db Emulator (Docker)
# 2. download and run Cosmos Db Emulator image:

docker pull mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:latest

$runParameters = @(
    "--publish", "8081:8081"
    "--publish", "10250-10255:10250-10255"
    "--name", "cosmosdb-windows-emulator"
    "--detach",
    "--env", "AZURE_COSMOS_EMULATOR_IP_ADDRESS_OVERRIDE=127.0.0.1"
    "--env", "AZURE_COSMOS_EMULATOR_ARGS=/DisableRateLimiting"
)

docker run @runParameters mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:latest

# might be required to install localhost SSL certificate:

$httpCallParameters = @{
    Uri = 'https://localhost:8081/_explorer/emulator.pem'
    Method = 'GET'
    OutFile = 'emulatorcert.crt'
    SkipCertificateCheck = $True
}

Invoke-WebRequest @httpCallParameters

$certificateParameters = @{
    FilePath = 'emulatorcert.crt'
    CertStoreLocation = 'Cert:\CurrentUser\Root'
}

Import-Certificate @certificateParameters
