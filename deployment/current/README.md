**Steps to deploy new instance of safeexchange:**

> **WARNING**: Steps do not contain setting up Azure Front Door that should be used with web-enabled storage acccount.

0. In order to have a nice domain name, please use Azure Front Door instance that points to static website, see here: [Configure a custom domain on Azure Front Door](https://learn.microsoft.com/en-us/azure/frontdoor/standard-premium/how-to-add-custom-domain)

1. Create Entra ID Application for authentication and information querying.
2. Create resource group **{GROUP NAME}** in Azure.
3. Fill out corresponding parameters in parameters.json.
4. Run **az cli** commands:

```
az login
az deployment group create --resource-group {GROUP NAME} --template-file ./deployment/arm/services-template.arm.json --parameters ./deployment/arm/services-parameters.arm.json
```
5. After successful deployment, deploy static website and backend to newly created azure function.

