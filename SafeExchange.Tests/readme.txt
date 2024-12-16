
# how to run unit tests locally:

# 1. install Cosmos Db Emulator
# https://learn.microsoft.com/en-us/azure/cosmos-db/emulator-release-notes

# 2. run Cosmos Db Emulator, open data explorer at https://localhost:8081/_explorer/index.html
# and specify connection string in user secrets.

# (might be required to install localhost SSL certificate, when running in docker)

Invoke-WebRequest -Uri 'https://localhost:8081/_explorer/emulator.pem' -SkipCertificateCheck -OutFile 'emulatorcert.crt'
Import-Certificate -FilePath 'emulatorcert.crt' -CertStoreLocation 'Cert:\CurrentUser\Root'
Remove-Item 'emulatorcert.crt'
