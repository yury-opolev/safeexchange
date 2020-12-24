# safeexchange
  This repo is holding sources for SafeExchange backend service, used for safe storage and exchange of secrets.

  SafeExchange backend consists of Azure Key Vault, Azure AD Application, and Azure Function App.

  Function App is used as backend API service in SafeExchange. In order to work properly, it needs a specific setup, where it is accessible for users with Azure AD Application, and where it can access Azure KeyVault.

  All sensitive data is stored in Azure KeyVault, buit it is not accessible to users directly. Instead, an Azure Function App is used to access it. Azure Function has kind of an 'authorization layer', where it has a set of permissions. In order to access secret value, a user must have a specific permission registered in Azure Function App.

Here are wiki pages to set up Azure AD App and resources for SafeExchange backend:

[Initial setup of Azure AD Application for SafeExchange](https://github.com/yury-opolev/safeexchange/wiki/Initial-Setup-of-Azure-AD-Application-for-SafeExchange)

[How to setup Azure Resources for SafeExchange backend](https://github.com/yury-opolev/safeexchange/wiki/How-to-setup-Azure-Resources-for-SafeExchange-backend)
