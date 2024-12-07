# safeexchange
  This repo is holding sources for SafeExchange backend service, used to store and exchange secrets in a secure way.

  SafeExchange backend consists of various Azure services and Microsoft Entra ID application.

  Function App is used as backend API service in SafeExchange, and is accessible for authorized users/applications with Microsoft Entra ID.

  All sensitive data is stored in Azure Storage account Blobs, encrypted via Azure KeyVault. In order to access a secret value, aa a prerequisite, a user must have a specific permissions configured in the backend service.

Here are wiki pages to set up Azure AD App and resources for SafeExchange backend:

[Initial setup of Azure AD Application for SafeExchange](https://github.com/yury-opolev/safeexchange/wiki/Initial-Setup-of-Azure-AD-Application-for-SafeExchange)

[How to setup Azure Resources for SafeExchange backend](https://github.com/yury-opolev/safeexchange/wiki/How-to-setup-Azure-Resources-for-SafeExchange-backend)

# Deploy to Azure

  Deploy all required services for safeexchange:

[![Deploy to Azure](/images/deploytoazure.png)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fyury-opolev%2Fsafeexchange%2Fmain%2Fdeployment%2Fcurrent%2Farm%2Fservices-template.arm.json)

  After deployment is completed, azure functions project should be built and deployed to created azure functions instance. Web client should be built and deployed to static web client storage account.
