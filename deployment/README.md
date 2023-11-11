**Steps to deploy new instance of safeexchange:**

As a prerequisite, you must have **Log Analytics Workspace** created before, it will require to specify its group and name in the deployment parameters.

1. Create Entra ID Application for authentication and information querying.
2. Create resource group **{GROUP NAME}** in Azure.
3. Fill out corresponsding parameters in parameters.json, can keep **'cdn_endpoint_origin_host'** value as is, it will be replaced later.
4. Run **az cli** commands:

```
az login
az deployment group create --resource-group {GROUP NAME} --template-file ./deployment/arm/template.json --parameters ./deployment/arm/parameters-test.json
```
5. After first deployment run, go to Azure portal, enable static website on **'safeexchweb'** storage account:
    * set 'Index document name' to **'index.html'**,
    * set 'Error document path' to **'index.html'**.
6. Delete existing CDN endpoint, set **'cdn_endpoint_origin_host'** parameter to the domain name of newly created static website, and run deployment again.
7. After successful deployment, deploy static website and backend to newly created azure function.
