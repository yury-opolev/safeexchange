# SafeExchange

Sharing passwords, API keys, certificates, and other secrets over email or chat is risky — they get saved in message history, forwarded by accident, and sit around forever with no way to control who sees them. **SafeExchange** fixes this. It gives your team a simple, secure way to share sensitive data with fine-grained access control, automatic expiration, and full audit trails — all backed by Azure's enterprise-grade encryption and identity services. You decide who can access what, for how long, and you can revoke access at any time.

## Key Features

- **Encrypted storage** — secrets and files are encrypted via Azure KeyVault and stored in Azure Blob Storage, never in plain text.
- **Fine-grained permissions** — grant read, write, or admin access to individual users or entire Entra ID groups, with the ability to revoke at any time.
- **Automatic expiration** — set a fixed expiration date or an idle timeout so secrets are automatically purged when no longer needed.
- **Access request workflows** — users can request access to secrets, and owners approve or deny with full visibility.
- **Enterprise identity** — authenticates with Microsoft Entra ID (Azure AD), so there are no extra passwords to manage.
- **Serverless and scalable** — runs on Azure Functions and Cosmos DB, scaling automatically with your organization's needs.

## Architecture Overview

SafeExchange backend is built on Azure serverless infrastructure:

| Component | Technology | Purpose |
|-----------|-----------|---------|
| API | Azure Functions v4 (.NET 8) | HTTP endpoints for all operations |
| Database | Azure Cosmos DB | Metadata, permissions, and access records |
| File Storage | Azure Blob Storage | Encrypted secret content and file chunks |
| Encryption | Azure KeyVault | Key management and data encryption |
| Identity | Microsoft Entra ID | Authentication and user/group resolution |
| Monitoring | Application Insights | Logging and diagnostics |

For detailed architecture documentation, see the [docs/](docs/) folder.

## Getting Started

### Prerequisites

- An Azure subscription
- A Microsoft Entra ID (Azure AD) tenant
- .NET 8.0 SDK

### Setup

1. **Create the Entra ID application** — follow the [Azure AD Application setup guide](https://github.com/yury-opolev/safeexchange/wiki/Initial-Setup-of-Azure-AD-Application-for-SafeExchange).

2. **Deploy Azure resources** — follow the [Azure Resources setup guide](https://github.com/yury-opolev/safeexchange/wiki/How-to-setup-Azure-Resources-for-SafeExchange-backend), or use the one-click deployment below.

3. **Build and deploy** the Azure Functions project to the created Functions instance.

4. **Build and deploy** the web client to the static web app storage account.

### Deploy to Azure

Deploy all required services with one click:

[![Deploy to Azure](/images/deploytoazure.png)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fyury-opolev%2Fsafeexchange%2Fmain%2Fdeployment%2Fcurrent%2Farm%2Fservices-template.arm.json)

## Project Structure

```
SafeExchange.Core/        — Domain models, business logic, permissions, and Azure integrations
SafeExchange.Functions/   — Azure Functions HTTP endpoints (the API layer)
SafeExchange.Tests/       — Unit and integration tests
deployment/               — ARM templates for Azure infrastructure
docs/                     — Architecture and design documentation
```

## License

BSD 3-Clause License. See [LICENSE](LICENSE) for details.
