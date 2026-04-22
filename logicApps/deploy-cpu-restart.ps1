param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [string]$Location = "westeurope",

    [Parameter(Mandatory = $true)]
    [string]$RoleAssignmentScope,

    [string]$LogicAppName = "cpu-autorestart-logicApp"
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
$templateFile = Join-Path $scriptDir "azuredeploy-cpu-restart.json"
Write-Host "Deploying CPU Auto-Restart Logic App..." -ForegroundColor Green

$deploymentName = "cpu-autorestart-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

$result = az deployment group create `
    --resource-group $ResourceGroupName `
    --name $deploymentName `
    --template-file $templateFile `
    --parameters logicAppName=$LogicAppName `
    2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Error "Deployment failed: $result"
    exit 1
}

$output = $result | ConvertFrom-Json
$principalId = $output.properties.outputs.logicAppPrincipalId.value

Write-Host "`nDeployment succeeded!" -ForegroundColor Green
Write-Host "Logic App Callback URL (use this in Azure Monitor Action Group):" -ForegroundColor Cyan
Write-Host $output.properties.outputs.logicAppCallbackUrl.value -ForegroundColor White
Write-Host "`nLogic App Resource ID:" -ForegroundColor Cyan
Write-Host $output.properties.outputs.logicAppResourceId.value -ForegroundColor White
Write-Host "`nManaged Identity Principal ID:" -ForegroundColor Cyan
Write-Host $principalId -ForegroundColor White

# Assign Virtual Machine Contributor role so the Logic App can restart any VM within scope
Write-Host "`nAssigning 'Virtual Machine Contributor' role to the Logic App's managed identity..." -ForegroundColor Yellow
Write-Host "Scope: $RoleAssignmentScope" -ForegroundColor DarkGray

$roleResult = az role assignment create `
    --assignee-object-id $principalId `
    --assignee-principal-type ServicePrincipal `
    --role "Virtual Machine Contributor" `
    --scope $RoleAssignmentScope `
    2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Warning "Role assignment failed. You may need to assign it manually:"
    Write-Host "  az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal --role 'Virtual Machine Contributor' --scope '$RoleAssignmentScope'" -ForegroundColor DarkGray
} else {
    Write-Host "Role assignment succeeded." -ForegroundColor Green
}

Write-Host "`n--- Next Steps ---" -ForegroundColor Cyan
Write-Host "1. Create metric alert rules in Azure Monitor for each VM (or use a single rule targeting multiple VMs):" -ForegroundColor White
Write-Host "   - Signal:         Percentage CPU" -ForegroundColor DarkGray
Write-Host "   - Operator:       Greater than or equal to" -ForegroundColor DarkGray
Write-Host "   - Threshold:      100" -ForegroundColor DarkGray
Write-Host "   - Aggregation:    Average" -ForegroundColor DarkGray
Write-Host "   - Window size:    5 minutes" -ForegroundColor DarkGray
Write-Host "   - Frequency:      1 minute" -ForegroundColor DarkGray
Write-Host "2. Create an Action Group that calls this Logic App's callback URL." -ForegroundColor White
Write-Host "3. Ensure the alert uses the Common Alert Schema." -ForegroundColor White
Write-Host "`nThe Logic App extracts the VM resource ID from the alert payload automatically." -ForegroundColor Yellow
Write-Host "Any VM within the role assignment scope ($RoleAssignmentScope) can be restarted." -ForegroundColor Yellow
