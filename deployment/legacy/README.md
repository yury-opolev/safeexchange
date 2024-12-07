**Steps to deploy new instance of safeexchange:**

1. Create Entra ID Application for authentication and information querying.
2. Create resource group **{GROUP NAME}** in Azure.
3. Fill out corresponding parameters in parameters.json.
4. Run **az cli** commands:

```
az login
az deployment group create --resource-group {GROUP NAME} --template-file ./deployment/arm/services-template.arm.json --parameters ./deployment/arm/services-parameters.arm.json
```
5. After successful deployment, deploy static website and backend to newly created azure function.
