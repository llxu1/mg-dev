# MCP Gateway Azure Deployment

This directory contains infrastructure-as-code templates and scripts for deploying the MCP Gateway to Azure.

## Deployment Options

There are two ways to deploy the MCP Gateway infrastructure:

### Option 1: PowerShell Script (Recommended)

Use the PowerShell deployment script for better control and separation of concerns. This approach:
- Deploys Azure infrastructure using Bicep
- Separately configures Kubernetes resources
- Provides better error handling and progress feedback

**Prerequisites:**
- Azure CLI installed and authenticated (`az login`)
- PowerShell 5.1 or higher
- Appropriate Azure permissions

**Basic Usage:**

```powershell
.\Deploy-McpGateway.ps1 -ResourceGroupName "rg-mcpgateway-dev" -ClientId "<your-entra-client-id>"
```

**Advanced Usage:**

```powershell
# Deploy to a specific region with a custom resource label
.\Deploy-McpGateway.ps1 `
    -ResourceGroupName "rg-mcpgateway-prod" `
    -ClientId "<your-entra-client-id>" `
    -ResourceLabel "mcpprod" `
    -Location "westus2"

# Deploy with private endpoints enabled
.\Deploy-McpGateway.ps1 `
    -ResourceGroupName "rg-mcpgateway-secure" `
    -ClientId "<your-entra-client-id>" `
    -EnablePrivateEndpoints
```

**Parameters:**

| Parameter | Required | Description |
|-----------|----------|-------------|
| `ResourceGroupName` | Yes | Name of the Azure resource group (created if doesn't exist) |
| `ClientId` | Yes | Entra ID client ID for authentication |
| `ResourceLabel` | No | Alphanumeric suffix for resource naming (3-30 chars). Defaults to resource group name |
| `Location` | No | Azure region for deployment. Default: `westus3` |
| `EnablePrivateEndpoints` | No | Switch to enable private endpoints for ACR and Cosmos DB |

### Option 2: Direct Bicep Deployment (Legacy)

Deploy directly using Bicep with the embedded deployment script:

```bash
# Create resource group
az group create --name rg-mcpgateway-dev --location eastus

# Deploy using Bicep
az deployment group create \
  --name mcpgateway-deployment \
  --resource-group rg-mcpgateway-dev \
  --template-file azure-deployment.bicep \
  --parameters clientId=<your-entra-client-id>
```

**With additional parameters:**

```bash
az deployment group create \
  --name mcpgateway-deployment \
  --resource-group rg-mcpgateway-dev \
  --template-file azure-deployment.bicep \
  --parameters \
    clientId=<your-entra-client-id> \
    resourceLabel=mcpdev \
    location=westus2 \
    enablePrivateEndpoints=true
```

**Disable embedded Kubernetes deployment script:**

```bash
az deployment group create \
  --name mcpgateway-deployment \
  --resource-group rg-mcpgateway-dev \
  --template-file azure-deployment.bicep \
  --parameters \
    clientId=<your-entra-client-id> \
    enableKubernetesDeploymentScript=false
```

## Deployed Resources

The deployment creates the following Azure resources:

### Core Infrastructure
- **Azure Kubernetes Service (AKS)**: Managed Kubernetes cluster
  - 2-node cluster with D4ds_v5 VMs
  - Azure RBAC enabled
  - OIDC issuer and Workload Identity enabled
  
- **Azure Container Registry (ACR)**: Container image storage
  - Standard SKU
  - Integrated with AKS for image pull

- **Azure Cosmos DB**: Document database for gateway state
  - Session consistency level
  - Three containers: AdapterContainer, CacheContainer, ToolContainer
  - Optional private endpoint support

### Networking
- **Virtual Network (VNet)**: Network isolation
  - 10.0.0.0/16 address space
  - AKS subnet (10.0.1.0/24)
  - Application Gateway subnet (10.0.2.0/24)
  - Private endpoint subnet (10.0.3.0/24)

- **Application Gateway**: Layer 7 load balancer
  - Standard_v2 SKU
  - HTTP frontend on port 80
  - Health probe for backend monitoring

- **Public IP**: Static public IP with DNS label

### Identity & Access
- **Managed Identities**:
  - Gateway service identity (with Cosmos DB data contributor role)
  - Admin identity (for AKS operations)
  - Workload identity (for pod-level authentication)
  
- **Federated Credentials**: For Kubernetes service account integration

### Monitoring
- **Application Insights**: Application monitoring and telemetry

## Networking Options

### Public Access (Default)
Resources are accessible over the internet with proper authentication.

### Private Endpoints
Enable with `-EnablePrivateEndpoints` flag:
- ACR and Cosmos DB accessible only within VNet
- Private DNS zones automatically configured
- Ideal for production environments requiring network isolation

## Post-Deployment

After successful deployment:

1. **Access the Gateway**: Use the FQDN from the deployment output
   ```
   http://<public-ip-dns-label>.<region>.cloudapp.azure.com
   ```

2. **Verify Kubernetes Pods**:
   ```bash
   kubectl get pods -n adapter
   ```

3. **Check Gateway Logs**:
   ```bash
   kubectl logs -n adapter -l app=mcpgateway-service
   ```

## Troubleshooting

### PowerShell Script Issues

**Prerequisites not met:**
- Ensure Azure CLI is installed: `az --version`
- Login to Azure: `az login`

**Deployment failures:**
- Check Azure CLI output for specific errors
- Verify you have appropriate permissions in the subscription
- Ensure the resource label is unique and meets naming requirements

**Kubernetes deployment issues:**
- Verify AKS cluster is running: `az aks show -g <rg-name> -n <aks-name>`
- Review pod status: `az aks command invoke -g <rg-name> -n <aks-name> --command "kubectl get pods -A"`

### Bicep Deployment Issues

**Template validation errors:**
```bash
az deployment group validate \
  --resource-group <rg-name> \
  --template-file azure-deployment.bicep \
  --parameters clientId=<your-client-id>
```

**Deployment script failures:**
- Check deployment script logs in Azure Portal
- Verify managed identity has appropriate permissions
- Review AKS cluster accessibility

## Cleanup

To remove all deployed resources:

```powershell
# Delete the entire resource group
az group delete --name rg-mcpgateway-dev --yes --no-wait
```

## Migration from Embedded Script to PowerShell

If you previously deployed using the embedded Bicep deployment script:

1. The Bicep template now supports both methods via the `enableKubernetesDeploymentScript` parameter
2. To update an existing deployment without the embedded script:
   ```powershell
   .\Deploy-McpGateway.ps1 -ResourceGroupName <existing-rg> -ClientId <client-id>
   ```
3. The PowerShell script will update the infrastructure and reconfigure Kubernetes resources

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      Internet                               │
└────────────────────────┬────────────────────────────────────┘
                         │
                    ┌────▼─────┐
                    │ Public IP│
                    └────┬─────┘
                         │
                ┌────────▼──────────┐
                │ Application       │
                │ Gateway           │
                └────────┬──────────┘
                         │
        ┌────────────────┼────────────────┐
        │                                 │
┌───────▼────────┐              ┌────────▼────────┐
│                │              │                 │
│  AKS Cluster   │──────────────│  ACR            │
│                │              │  (Images)       │
└───────┬────────┘              └─────────────────┘
        │
        │ Workload Identity
        │
┌───────▼────────┐              ┌─────────────────┐
│                │              │                 │
│  Cosmos DB     │              │  App Insights   │
│  (State)       │              │  (Monitoring)   │
└────────────────┘              └─────────────────┘
```

## Security Considerations

- **Authentication**: Uses Entra ID (Azure AD) for authentication
- **Authorization**: Azure RBAC for AKS, Cosmos DB RBAC for data access
- **Network Isolation**: Optional private endpoints for enhanced security
- **Identity**: Workload Identity for pod-level authentication (no secrets needed)
- **Secrets**: Managed identities eliminate need for storing credentials

## Additional Resources

- [Azure Kubernetes Service Documentation](https://docs.microsoft.com/azure/aks/)
- [Azure Container Registry Documentation](https://docs.microsoft.com/azure/container-registry/)
- [Azure Cosmos DB Documentation](https://docs.microsoft.com/azure/cosmos-db/)
- [Bicep Documentation](https://docs.microsoft.com/azure/azure-resource-manager/bicep/)
