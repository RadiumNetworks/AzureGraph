## Deploy Logic app that can be triggered by a cpu metric treshold but adds additional data using a KQL query

```powershell

.\deploy-cpu-kql.ps1 -ResourceGroupName "monitoring" `
    -TargetEndpointUrl "https://webapi-<uniqueid>.<region>.azurewebsites.net/api/alerts" `
    -LogAnalyticsWorkspaceId "/subscriptions/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/resourceGroups/Monitoring/providers/Microsoft.OperationalInsights/workspaces/LogAnalyticsWS1" `
    -LogAnalyticsWorkspaceGuid "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"

```
> az role assignment create --assignee <principalId> --role 'Log Analytics Reader' `
  --scope '/subscriptions/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/resourceGroups/Monitoring/providers/Microsoft.OperationalInsights/workspaces/LogAnalyticsWS1'



### included KQL query

```kql

Perf
| where Computer == "<vmName>"
| where ObjectName == "Processor" and CounterName == "% Processor Time" and InstanceName == "_Total"
| where TimeGenerated > ago(1h)
| project TimeGenerated, Computer, CounterValue
| order by TimeGenerated desc
| take 20

```

## Deploy logic app that triggers a VM restart after a CPU metric threshold is met

```powershell

# Scope to an entire subscription
.\deploy-cpu-restart.ps1 -ResourceGroupName "monitoring" `
    -RoleAssignmentScope "/subscriptions/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"

# Scope to an entire resource group (any VM in it can be restarted)
.\deploy-cpu-restart.ps1 -ResourceGroupName "monitoring" `
    -RoleAssignmentScope "/subscriptions/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/resourceGroups/infra"

# Scope to a single VM
.\deploy-cpu-restart.ps1 -ResourceGroupName "monitoring" `
    -RoleAssignmentScope "/subscriptions/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/resourceGroups/infra/providers/Microsoft.Compute/virtualMachines/DC1"

// Assigning 'Virtual Machine Contributor' role to the Logic App's managed identity...
// Scope: /subscriptions/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/resourceGroups/infra/providers/Microsoft.Compute/virtualMachines/DC1>


```

1. Create a metric alert rule in Azure Monitor:
   - Resource:       /subscriptions/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/resourceGroups/infra/providers/Microsoft.Compute/virtualMachines/DC1
   - Signal:         Percentage CPU
   - Operator:       Greater than or equal to
   - Threshold:      100
   - Aggregation:    Average
   - Window size:    5 minutes
   - Frequency:      1 minute
2. Create an Action Group that calls this Logic App's callback URL.
3. Ensure the alert uses the Common Alert Schema.


## Deploy a logic app that is triggered on schedule, reading all application and writing alerts about expiring certifiates to

### Create custom table

> az deployment group create `
    --resource-group "monitoring" `
    --template-file setup-custom-table.json `
    --parameters workspaceName="LogAnalyticsWS1"


### Create data collection endpoint

> az deployment group create `
    --resource-group "monitoring" `
    --template-file setup-dce.json `
    --parameters dataCollectionEndpointName="dce-credential-expiry"

### Create data collection rule

> az deployment group create `
    --resource-group "monitoring" `
    --template-file setup-dcr.json `
    --parameters workspaceResourceId="/subscriptions/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/resourcegroups/monitoring/providers/microsoft.operationalinsights/workspaces/loganalyticsws1" `
                 dataCollectionEndpointResourceId="/subscriptions/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/resourceGroups/Monitoring/providers/Microsoft.Insights/dataCollectionEndpoints/dce-credential-expiry"

### Deploy logc app 
```powershell

.\deploy-credential-expiry.ps1 -ResourceGroupName "monitoring" `
    -DataCollectionEndpointUrl "https://dce-<component>.<region>.ingest.monitor.azure.com" `
    -DataCollectionRuleId "dcr-<id>" `
    -DataCollectionRuleResourceId "/subscriptions/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/resourceGroups/Monitoring/providers/Microsoft.Insights/dataCollectionRules/dcr-credential-expiry"
```

#### possible query to the custom table

```kql

AppCredentialExpiry_CL
| sort by ExpirationDate asc
| project AppDisplayName, AppId, ObjectId, CredentialType, CredentialId, ExpirationDate, DaysUntilExpiry

```