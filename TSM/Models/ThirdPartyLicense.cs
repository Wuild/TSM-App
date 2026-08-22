namespace TSM.Models;

public sealed record ThirdPartyLicense(
    string Project,
    string Version,
    string License,
    string Purpose,
    string ProjectUrl,
    string LicenseUrl);
