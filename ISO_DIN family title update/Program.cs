using Dapper;
using MySqlConnector;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Serilog;
using Polly;
using Polly.Retry;

public class Program
{
    // =================================================================================================
    //  C# Utility to Find and Report ISO/DIN Naming Inconsistencies
    //  Version: 1.0 (Preview Mode)
    //  Purpose: This application connects to a MariaDB database, identifies family titles
    //           with ISO standards that are missing their corresponding DIN equivalents,
    //           and prints the proposed changes to the console.
    // =================================================================================================

    public static async Task Main(string[] args)
    {
        // Setup Serilog for structured logging
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();

        // Database connection string
        string connectionString = "server=10.103.144.17;" +
                                  "port=3308;" +
                                  "user=wurth;" +
                                  "password=WurthSql20101!;" +
                                  "database=eshop;" +
                                  "AllowUserVariables=true;";

        Log.Information("--- ISO/DIN Family Title Update Utility (Preview Mode) ---");
        var stopwatch = new Stopwatch();

        // Polly retry policy for handling transient database connection errors
        var retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    Log.Warning($"Retry {retryCount} after {timeSpan.TotalSeconds}s due to: {exception.Message}");
                });

        await using (var connection = new MySqlConnection(connectionString))
        {
            try
            {
                await retryPolicy.ExecuteAsync(async () =>
                {
                    Log.Information("Connecting to database...");
                    await connection.OpenAsync();
                    Log.Information("Connection successful.\n");
                });

                stopwatch.Start();
                Log.Information("Step 1: Finding and calculating potential family title updates...");

                // The main method that performs the analysis and prints the results
                int changesFound = await FindAndDisplayIsoDinUpdates(connection);

                stopwatch.Stop();
                Log.Information($"Analysis complete in {stopwatch.Elapsed.TotalSeconds:F2} seconds.");

                if (changesFound == 0)
                {
                    Log.Information("No families require title updates. Database is consistent.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AN ERROR OCCURRED: {Message}", ex.Message);
                Log.Information("Please check your connection string and ensure the database server is running.");
            }
        }

        Log.Information("\nProcessing complete. Press any key to exit.");
        Console.ReadKey();
        Log.CloseAndFlush();
    }

    /// <summary>
    /// Finds families with ISO/DIN inconsistencies, calculates the correct title,
    /// and prints a preview of the changes. This revised version uses a more
    /// robust replacement strategy to prevent duplication errors.
    /// </summary>
    /// <returns>The number of families that need updating.</returns>
    private static async Task<int> FindAndDisplayIsoDinUpdates(MySqlConnection connection)
    {
        Log.Information("  - Fetching families with 'ISO' or 'DIN' in their title...");
        var families = await connection.QueryAsync<Family>(
            "SELECT id, title FROM family WHERE title LIKE '%ISO%' OR title LIKE '%DIN%'"
        );
        Log.Information($"  - Found {families.Count()} potential families to analyze.");

        var standardMappings = GetStandardMappings();
        var updatesToPreview = new List<FamilyUpdatePreview>();
        int skippedRecords = 0;

        foreach (var family in families)
        {
            string originalTitle = family.Title;
            string newTitle = originalTitle;
            bool mappingApplied = false;

            // Find all DINs in the title using a capturing group for the number
            var dinRegex = new Regex(@"\bDIN\s*(\d+)\b", RegexOptions.IgnoreCase);
            var dinMatches = dinRegex.Matches(newTitle);

            // Find all ISOs in the title using a capturing group for the number
            var isoRegex = new Regex(@"\bISO\s*(\d+)\b", RegexOptions.IgnoreCase);
            var isoMatches = isoRegex.Matches(newTitle);

            // If there is a valid ISO XXX/DIN XXX pair, only fix spacing/commas
            bool hasValidPair = false;
            foreach (var mapping in standardMappings)
            {
                var isoPattern = $"ISO\\s*{mapping.IsoUnspaced.Substring(3)}";
                var dinPattern = $"DIN\\s*{mapping.DinAppend.Replace("/DIN ", "").Trim()}";
                var pairPattern = $"{isoPattern}\\s*/\\s*{dinPattern}";
                if (Regex.IsMatch(newTitle, pairPattern, RegexOptions.IgnoreCase))
                {
                    // Only fix spacing and formatting
                    newTitle = Regex.Replace(newTitle, pairPattern, $"{mapping.IsoSpaced}/{mapping.DinAppend.Replace("/", "")}", RegexOptions.IgnoreCase);
                    hasValidPair = true;
                    break;
                }
            }

            if (!hasValidPair)
            {
                // --- original mapping logic ---
                // Extract numbers from the captured groups, not by splitting
                var isoNumbers = isoMatches.Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                var dinNumbers = dinMatches.Cast<Match>().Select(m => m.Groups[1].Value).ToList();

                if (dinMatches.Count > 0)
                {
                    string dinNum = dinNumbers[0];
                    var mapping = standardMappings.FirstOrDefault(m => {
                        var dinMappingNum = m.DinAppend.Replace("/DIN ", "").Trim();
                        return dinMappingNum == dinNum;
                    });
                    if (mapping != null)
                    {
                        var isoInTitle = isoNumbers.FirstOrDefault(n => $"ISO{n}" == mapping.IsoUnspaced);
                        string isoToUse = isoInTitle != null ? $"ISO {isoInTitle}" : mapping.IsoSpaced;
                        var otherIsoNumbers = isoNumbers.Where(n => $"ISO{n}" != mapping.IsoUnspaced).ToList();
                        string isoPart = isoToUse;
                        if (otherIsoNumbers.Count > 0)
                            isoPart += "/" + string.Join("/", otherIsoNumbers);
                        var mappedDinNum = mapping.DinAppend.Replace("/DIN ", "").Trim();
                        var otherDinNumbers = dinNumbers.Where(n => n != mappedDinNum).ToList();
                        string dinPart = $"DIN {mappedDinNum}";
                        if (otherDinNumbers.Count > 0)
                            dinPart += "/" + string.Join("/", otherDinNumbers);
                        newTitle = isoPart + "/" + dinPart;
                        var lastDinMatch = dinMatches.Count > 0 ? dinMatches[dinMatches.Count - 1] : null;
                        var lastIsoMatch = isoMatches.Count > 0 ? isoMatches[isoMatches.Count - 1] : null;
                        int lastIndex = Math.Max(lastDinMatch?.Index + lastDinMatch?.Length ?? 0, lastIsoMatch?.Index + lastIsoMatch?.Length ?? 0);
                        if (lastIndex < originalTitle.Length)
                            newTitle += " " + originalTitle.Substring(lastIndex).Trim();
                        mappingApplied = true;
                    }
                    else
                    {
                        skippedRecords++;
                    }
                }
                else if (isoMatches.Count > 0)
                {
                    string isoNum = isoNumbers[0];
                    var mapping = standardMappings.FirstOrDefault(m => m.IsoUnspaced == $"ISO{isoNum}");
                    if (mapping != null)
                    {
                        var otherIsoNumbers = isoNumbers.Where(n => $"ISO{n}" != mapping.IsoUnspaced).ToList();
                        string isoPart = mapping.IsoSpaced;
                        if (otherIsoNumbers.Count > 0)
                            isoPart += "/" + string.Join("/", otherIsoNumbers);
                        var mappedDinNum = mapping.DinAppend.Replace("/DIN ", "").Trim();
                        var otherDinNumbers = dinNumbers.Where(n => n != mappedDinNum).ToList();
                        string dinPart = $"DIN {mappedDinNum}";
                        if (otherDinNumbers.Count > 0)
                            dinPart += "/" + string.Join("/", otherDinNumbers);
                        newTitle = isoPart + "/" + dinPart;
                        var lastDinMatch = dinMatches.Count > 0 ? dinMatches[dinMatches.Count - 1] : null;
                        var lastIsoMatch = isoMatches.Count > 0 ? isoMatches[isoMatches.Count - 1] : null;
                        int lastIndex = Math.Max(lastDinMatch?.Index + lastDinMatch?.Length ?? 0, lastIsoMatch?.Index + lastIsoMatch?.Length ?? 0);
                        if (lastIndex < originalTitle.Length)
                            newTitle += " " + originalTitle.Substring(lastIndex).Trim();
                        mappingApplied = true;
                    }
                    else
                    {
                        skippedRecords++;
                    }
                }
                else
                {
                    skippedRecords++;
                }
            }

            // --- Spacing fixes ---
            newTitle = Regex.Replace(newTitle, @"(DIN|ISO)\s*(\d+)", "$1 $2");
            newTitle = Regex.Replace(newTitle, @"\s+/\s+", "/");
            newTitle = Regex.Replace(newTitle, @"\s{2,}", " ");
            newTitle = newTitle.Trim();

            if (originalTitle != newTitle)
            {
                updatesToPreview.Add(new FamilyUpdatePreview
                {
                    FamilyId = family.Id,
                    OriginalTitle = originalTitle,
                    NewTitle = newTitle
                });
            }
        }

        Log.Information($"\nStep 2: Displaying the {updatesToPreview.Count} families that need updates...\n");
        if (skippedRecords > 0)
            Log.Warning($"Skipped {skippedRecords} records due to missing mapping or no ISO/DIN found.");

        if (updatesToPreview.Any())
        {
            foreach (var update in updatesToPreview)
            {
                Console.WriteLine("--------------------------------------------------");
                Console.WriteLine($"  Family ID: {update.FamilyId}");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Original : {update.OriginalTitle}");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  Proposed : {update.NewTitle}");
                Console.ResetColor();
            }
            Console.WriteLine("--------------------------------------------------\n");

            // Ask user for confirmation
            Console.Write("Do you want to update these changes to the database? (Y/N): ");
            var key = Console.ReadKey();
            Console.WriteLine();
            if (key.Key == ConsoleKey.Y)
            {
                Log.Information("Updating changes to the database in a transaction...");
                int updatedCount = 0;
                await using (var transaction = await connection.BeginTransactionAsync())
                {
                    try
                    {
                        foreach (var update in updatesToPreview)
                        {
                            await connection.ExecuteAsync(
                                "UPDATE family SET title = @NewTitle WHERE id = @Id",
                                new { NewTitle = update.NewTitle, Id = update.FamilyId },
                                transaction: transaction
                            );
                            updatedCount++;
                        }
                        await transaction.CommitAsync();
                        Log.Information($"Database update complete. {updatedCount} records updated.");
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        Log.Error(ex, "Transaction failed. All changes rolled back.");
                    }
                }
            }
            else
            {
                Log.Information("No changes were made to the database.");
            }
        }
        return updatesToPreview.Count;
    }
    private static List<StandardMapping> GetStandardMappings()
    {
        return new List<StandardMapping>
        {
            new StandardMapping("ISO 1207", "ISO1207", "/DIN 84"),
            new StandardMapping("ISO 1234", "ISO1234", "/DIN 94"),
            new StandardMapping("ISO 1479", "ISO1479", "/DIN 7976"),
            new StandardMapping("ISO 1481", "ISO1481", "/DIN 7971"),
            new StandardMapping("ISO 1482", "ISO1482", "/DIN 7972"),
            new StandardMapping("ISO 1483", "ISO1483", "/DIN 7973"),
            new StandardMapping("ISO 1580", "ISO1580", "/DIN 85"),
            new StandardMapping("ISO 2009", "ISO2009", "/DIN 963"),
            new StandardMapping("ISO 2010", "ISO2010", "/DIN 964"),
            new StandardMapping("ISO 2338", "ISO2338", "/DIN 7"),
            new StandardMapping("ISO 2339", "ISO2339", "/DIN 1"),
            new StandardMapping("ISO 2340", "ISO2340", "/DIN 1443"),
            new StandardMapping("ISO 2341", "ISO2341", "/DIN 1444"),
            new StandardMapping("ISO 2342", "ISO2342", "/DIN 427"),
            new StandardMapping("ISO 2936", "ISO2936", "/DIN 911"),
            new StandardMapping("ISO 4014", "ISO4014", "/DIN 931"),
            new StandardMapping("ISO 4017", "ISO4017", "/DIN 933"),
            new StandardMapping("ISO 4018", "ISO4018", "/DIN 558"),
            new StandardMapping("ISO 4026", "ISO4026", "/DIN 913"),
            new StandardMapping("ISO 4027", "ISO4027", "/DIN 914"),
            new StandardMapping("ISO 4028", "ISO4028", "/DIN 915"),
            new StandardMapping("ISO 4029", "ISO4029", "/DIN 916"),
            new StandardMapping("ISO 4032", "ISO4032", "/DIN 934"),
            new StandardMapping("ISO 4161", "ISO4161", "/DIN 6923"),
            new StandardMapping("ISO 4762", "ISO4762", "/DIN 912"),
            new StandardMapping("ISO 4766", "ISO4766", "/DIN 551"),
            new StandardMapping("ISO 7040", "ISO7040", "/DIN 982"),
            new StandardMapping("ISO 7042", "ISO7042", "/DIN 980"),
            new StandardMapping("ISO 7043", "ISO7043", "/DIN 6926"),
            new StandardMapping("ISO 7044", "ISO7044", "/DIN 6927"),
            new StandardMapping("ISO 7045", "ISO7045", "/DIN 7985"),
            new StandardMapping("ISO 7046", "ISO7046", "/DIN 965"),
            new StandardMapping("ISO 7047", "ISO7047", "/DIN 966"),
            new StandardMapping("ISO 7048", "ISO7048", "/DIN 7500"),
            new StandardMapping("ISO 7049", "ISO7049", "/DIN 7981"),
            new StandardMapping("ISO 7050", "ISO7050", "/DIN 7982"),
            new StandardMapping("ISO 7051", "ISO7051", "/DIN 7983"),
            new StandardMapping("ISO 7089", "ISO7089", "/DIN 125"),
            new StandardMapping("ISO 7091", "ISO7091", "/DIN 126"),
            new StandardMapping("ISO 7092", "ISO7092", "/DIN 433"),
            new StandardMapping("ISO 7434", "ISO7434", "/DIN 417"),
            new StandardMapping("ISO 7435", "ISO7435", "/DIN 553"),
            new StandardMapping("ISO 7436", "ISO7436", "/DIN 438"),
            new StandardMapping("ISO 8675", "ISO8675", "/DIN 936"),
            new StandardMapping("ISO 8734", "ISO8734", "/DIN 6325"),
            new StandardMapping("ISO 8736", "ISO8736", "/DIN 7978"),
            new StandardMapping("ISO 8737", "ISO8737", "/DIN 7977"),
            new StandardMapping("ISO 8738", "ISO8738", "/DIN 1440"),
            new StandardMapping("ISO 8739", "ISO8739", "/DIN 1470"),
            new StandardMapping("ISO 8740", "ISO8740", "/DIN 1473"),
            new StandardMapping("ISO 8741", "ISO8741", "/DIN 1474"),
            new StandardMapping("ISO 8742", "ISO8742", "/DIN 1475"),
            new StandardMapping("ISO 8744", "ISO8744", "/DIN 1471"),
            new StandardMapping("ISO 8745", "ISO8745", "/DIN 1472"),
            new StandardMapping("ISO 8746", "ISO8746", "/DIN 1476"),
            new StandardMapping("ISO 8747", "ISO8747", "/DIN 1477"),
            new StandardMapping("ISO 8750", "ISO8750", "/DIN 7343"),
            new StandardMapping("ISO 8752", "ISO8752", "/DIN 1481"),
            new StandardMapping("ISO 8765", "ISO8765", "/DIN 960"),
            new StandardMapping("ISO 10510", "ISO10510", "/DIN 7967"),
            new StandardMapping("ISO 10511", "ISO10511", "/DIN 985"),
            new StandardMapping("ISO 10642", "ISO10642", "/DIN 7991"),
            new StandardMapping("ISO 12125", "ISO12125", "/DIN 6926"),
            new StandardMapping("ISO 12126", "ISO12126", "/DIN 6927"),
            new StandardMapping("ISO 13337", "ISO13337", "/DIN 7346"),
        };
    }
}

// =================================================================================================
// Data Models for Dapper and Logic
// =================================================================================================

/// <summary>
/// Represents a record from the 'family' table.
/// </summary>
public class Family
{
    public int Id { get; set; }
    public string Title { get; set; }
}

/// <summary>
/// Represents a mapping between an ISO and DIN standard.
/// </summary>
public class StandardMapping
{
    public string IsoSpaced { get; }
    public string IsoUnspaced { get; }
    public string DinAppend { get; }

    public StandardMapping(string isoSpaced, string isoUnspaced, string dinAppend)
    {
        IsoSpaced = isoSpaced;
        IsoUnspaced = isoUnspaced;
        DinAppend = dinAppend;
    }
}

/// <summary>
/// A helper class to store the original and proposed new title for a family.
/// </summary>
public class FamilyUpdatePreview
{
    public int FamilyId { get; set; }
    public string OriginalTitle { get; set; }
    public string NewTitle { get; set; }
}