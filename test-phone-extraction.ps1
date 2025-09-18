# PowerShell script to test phone extraction with debug output
Write-Host "Testing Phone Extraction Logic" -ForegroundColor Green
Write-Host "==============================" -ForegroundColor Green

# Create a test profile with phone mappings
$profileJson = @'
{
  "Id": "test-phone-profile",
  "Name": "Test Phone Profile",
  "ContactMappings": [
    {
      "SourceColumn": "First Name",
      "HubSpotProperty": "First Name",
      "AssociationType": "Owner"
    },
    {
      "SourceColumn": "Last Name",
      "HubSpotProperty": "Last Name",
      "AssociationType": "Owner"
    },
    {
      "SourceColumn": "TloPhone1",
      "HubSpotProperty": "Phone Number",
      "AssociationType": "Owner"
    },
    {
      "SourceColumn": "TloPhone1Type",
      "HubSpotProperty": "Phone Type",
      "AssociationType": "Owner"
    },
    {
      "SourceColumn": "TloPhone2",
      "HubSpotProperty": "Phone Number",
      "AssociationType": "Owner"
    },
    {
      "SourceColumn": "TloPhone2Type",
      "HubSpotProperty": "Phone Type",
      "AssociationType": "Owner"
    }
  ],
  "PropertyMappings": [
    {
      "SourceColumn": "Property Address",
      "HubSpotProperty": "Address",
      "AssociationType": "Mailing Address"
    },
    {
      "SourceColumn": "Property City",
      "HubSpotProperty": "City",
      "AssociationType": "Mailing Address"
    },
    {
      "SourceColumn": "Property State",
      "HubSpotProperty": "State",
      "AssociationType": "Mailing Address"
    },
    {
      "SourceColumn": "Property Zip",
      "HubSpotProperty": "Zip",
      "AssociationType": "Mailing Address"
    }
  ],
  "PhoneMappings": []
}
'@

# Save test profile
$profilePath = "profiles\test-phone-profile.json"
New-Item -Path "profiles" -ItemType Directory -Force | Out-Null
$profileJson | Out-File -FilePath $profilePath -Encoding UTF8

Write-Host "`nTest profile saved to: $profilePath" -ForegroundColor Yellow
Write-Host "`nProfile contains:" -ForegroundColor Cyan
Write-Host "- 2 Contact mappings (First Name, Last Name)" -ForegroundColor White
Write-Host "- 4 Phone mappings (TloPhone1, TloPhone1Type, TloPhone2, TloPhone2Type)" -ForegroundColor White
Write-Host "  Note: Phone mappings are in ContactMappings with AssociationType='Owner'" -ForegroundColor Gray
Write-Host "- 4 Property mappings (Address, City, State, Zip)" -ForegroundColor White

Write-Host "`nTest data file: test-data\test-phones.csv" -ForegroundColor Yellow
Write-Host "Contains 3 records with phone numbers" -ForegroundColor White

Write-Host "`n===========================================" -ForegroundColor Green
Write-Host "To test phone extraction:" -ForegroundColor Green
Write-Host "1. Run the TriSplit application" -ForegroundColor White
Write-Host "2. Go to Profiles tab" -ForegroundColor White
Write-Host "3. Load 'Test Phone Profile'" -ForegroundColor White
Write-Host "4. Go to Processing tab" -ForegroundColor White
Write-Host "5. Select test-data\test-phones.csv" -ForegroundColor White
Write-Host "6. Click Start Processing" -ForegroundColor White
Write-Host "7. Check the output files in Exports folder" -ForegroundColor White
Write-Host "`nExpected: 02_Phone_Numbers_Import.csv should contain 4 phone records" -ForegroundColor Yellow
Write-Host "(2 phones for John Smith, 1 for Jane Doe, 1 for Corporate Holdings)" -ForegroundColor Gray