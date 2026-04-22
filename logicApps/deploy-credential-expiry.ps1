param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [string]$Location = "westeurope",

    [Parameter(Mandatory = $true)]
    [string]$DataCollectionEndpointUrl,

    [Parameter(Mandatory = $true)]
    [string]$DataCollectionRuleId,

    [Parameter(Mandatory = $true)]
    [string]$DataCollectionRuleResourceId,

    [string]$CustomTableStreamName = "Custom-AppCredentialExpiry_CL",

    [int]$ExpiryThresholdDays = 30,

    [int]$RecurrenceHour = 7,

    [string]$LogicAppName = "credential-expiry-monitor-logicApp"
)
$ErrorActionPreference = "Stop"

# Check Azure CLI login
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Error "Not logged in to Azure CLI. Run 'az login' first."
    exit 1
}
Write-Host "Using subscription: $($account.name) ($($account.id))" -ForegroundColor Cyan

# Create resource group if needed
$rgExists = az group exists --name $ResourceGroupName | ConvertFrom-Json
if (-not $rgExists) {
    Write-Host "Creating resource group '$ResourceGroupName' in '$Location'..." -ForegroundColor Yellow
    az group create --name $ResourceGroupName --location $Location | Out-Null
}

# Deploy the Logic App
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$templateFile = Join-Path $scriptDir "azuredeploy-credential-expiry.json"
Write-Host "Deploying Credential Expiry Monitor Logic App..." -ForegroundColor Green

$deploymentName = "cred-expiry-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

$result = az deployment group create `
    --resource-group $ResourceGroupName `
    --name $deploymentName `
    --template-file $templateFile `
    --parameters logicAppName=$LogicAppName `
    --parameters dataCollectionEndpointUrl=$DataCollectionEndpointUrl `
    --parameters dataCollectionRuleId=$DataCollectionRuleId `
    --parameters dataCollectionRuleResourceId=$DataCollectionRuleResourceId `
    --parameters customTableStreamName=$CustomTableStreamName `
    --parameters expiryThresholdDays=$ExpiryThresholdDays `
    --parameters recurrenceHour=$RecurrenceHour `
    2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Error "Deployment failed: $result"
    exit 1
}

$output = $result | ConvertFrom-Json
$principalId = $output.properties.outputs.logicAppPrincipalId.value

Write-Host "`nDeployment succeeded!" -ForegroundColor Green
Write-Host "Logic App Resource ID:" -ForegroundColor Cyan
Write-Host $output.properties.outputs.logicAppResourceId.value -ForegroundColor White
Write-Host "`nManaged Identity Principal ID:" -ForegroundColor Cyan
Write-Host $principalId -ForegroundColor White

# Assign Monitoring Metrics Publisher on the DCR
Write-Host "`n--- Assigning 'Monitoring Metrics Publisher' role on DCR ---" -ForegroundColor Yellow
$dcrRoleResult = az role assignment create `
    --assignee-object-id $principalId `
    --assignee-principal-type ServicePrincipal `
    --role "Monitoring Metrics Publisher" `
    --scope $DataCollectionRuleResourceId `
    2>&1

if ($LASTEXITCODE -eq 0) {
    Write-Host "Monitoring Metrics Publisher role assigned successfully." -ForegroundColor Green
} else {
    Write-Warning "Could not assign Monitoring Metrics Publisher role. You may need to assign it manually:"
    Write-Host "  az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal --role 'Monitoring Metrics Publisher' --scope '$DataCollectionRuleResourceId'" -ForegroundColor DarkGray
}

# Grant Microsoft Graph Application.Read.All to the managed identity
Write-Host "`n--- Granting Microsoft Graph API Permission ---" -ForegroundColor Yellow
Write-Host "The Logic App's managed identity needs 'Application.Read.All' on Microsoft Graph." -ForegroundColor White
Write-Host "This requires an admin to grant the app role assignment." -ForegroundColor White

$graphAppId = "00000003-0000-0000-c000-000000000000"  # Microsoft Graph well-known app ID
$appRoleName = "Application.Read.All"

# Get the service principal for Microsoft Graph
$graphSp = az ad sp show --id $graphAppId 2>$null | ConvertFrom-Json
if ($graphSp) {
    $appRole = ($graphSp.appRoles | Where-Object { $_.value -eq $appRoleName -and $_.allowedMemberTypes -contains "Application" })
    if ($appRole) {
        Write-Host "Assigning '$appRoleName' (role ID: $($appRole.id)) to managed identity..." -ForegroundColor Yellow

        $body = @{
            principalId = $principalId
            resourceId  = $graphSp.id
            appRoleId   = $appRole.id
        } | ConvertTo-Json -Compress

        $bodyFile = Join-Path $env:TEMP "graph-role-assignment.json"
        $body | Out-File -FilePath $bodyFile -Encoding utf8 -Force

        $assignResult = az rest `
            --method POST `
            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$principalId/appRoleAssignments" `
            --headers "Content-Type=application/json" `
            --body "@$bodyFile" `
            2>&1

        Remove-Item -Path $bodyFile -Force -ErrorAction SilentlyContinue

        if ($LASTEXITCODE -eq 0) {
            Write-Host "Graph API permission granted successfully." -ForegroundColor Green
        } else {
            Write-Warning "Could not assign Graph API permission automatically."
            Write-Host "You may need Global Admin or Privileged Role Admin rights." -ForegroundColor DarkGray
            Write-Host "Manual command:" -ForegroundColor DarkGray
            Write-Host "  az rest --method POST --uri 'https://graph.microsoft.com/v1.0/servicePrincipals/$principalId/appRoleAssignments' --body '$body'" -ForegroundColor DarkGray
        }
    }
} else {
    Write-Warning "Could not find the Microsoft Graph service principal. Grant permissions manually."
}

Write-Host "`n--- Configuration Summary ---" -ForegroundColor Cyan
Write-Host "Schedule:            Daily at ${RecurrenceHour}:00 UTC" -ForegroundColor White
Write-Host "Expiry threshold:    $ExpiryThresholdDays days" -ForegroundColor White
Write-Host "DCE:                 $DataCollectionEndpointUrl" -ForegroundColor White
Write-Host "DCR:                 $DataCollectionRuleId" -ForegroundColor White
Write-Host "Stream:              $CustomTableStreamName" -ForegroundColor White
Write-Host "`nSample KQL query to view results:" -ForegroundColor Cyan
Write-Host "  AppCredentialExpiry_CL | sort by ExpirationDate asc | project AppDisplayName, AppId, ObjectId, CredentialType, CredentialId, ExpirationDate, DaysUntilExpiry" -ForegroundColor DarkGray
Write-Host "`n--- Prerequisites ---" -ForegroundColor Cyan
Write-Host "Before deploying, you must create:" -ForegroundColor White
Write-Host "1. A custom table 'AppCredentialExpiry_CL' in your Log Analytics workspace" -ForegroundColor White
Write-Host "2. A Data Collection Endpoint (DCE)" -ForegroundColor White
Write-Host "3. A Data Collection Rule (DCR) that maps the stream to the custom table" -ForegroundColor White
Write-Host "See: https://learn.microsoft.com/en-us/azure/azure-monitor/logs/logs-ingestion-api-overview" -ForegroundColor DarkGray
