# safeexchange
  This repo is holding sources for SafeExchange backend service, used to store and exchange secrets in a secure way.

  SafeExchange backend consists of various Azure services and Microsoft Entra ID application.

  Function App is used as backend API service in SafeExchange, and is accessible for authorized users/applications with Microsoft Entra ID.

  All sensitive data is stored in Azure Storage account Blobs, encrypted via Azure KeyVault. In order to access a secret value, aa a prerequisite, a user must have a specific permissions configured in the backend service.

Here are wiki pages to set up Azure AD App and resources for SafeExchange backend:

[Initial setup of Azure AD Application for SafeExchange](https://github.com/yury-opolev/safeexchange/wiki/Initial-Setup-of-Azure-AD-Application-for-SafeExchange)

[How to setup Azure Resources for SafeExchange backend](https://github.com/yury-opolev/safeexchange/wiki/How-to-setup-Azure-Resources-for-SafeExchange-backend)
