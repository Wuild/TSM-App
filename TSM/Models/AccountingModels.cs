namespace TSM.Models;

public sealed record TsmAccount(
    string DisplayName,
    string AccountName,
    string GameVersion,
    string SavedVariablesPath,
    string AppHelperSavedVariablesPath = "",
    string AppDataPath = "");

public sealed record AccountingExportOptions(
    bool Sales,
    bool Purchases,
    bool Income,
    bool Expenses,
    bool Expired,
    bool Canceled);

public sealed record AccountingExportResult(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> MissingCategories,
    IReadOnlyList<string> Errors);
