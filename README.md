# E-shop ISO/DIN Title Corrector for family table

A .NET console utility designed to maintain data consistency by correcting ISO and DIN standard naming conventions in product family titles. The tool connects to a MariaDB database, identifies titles with incorrect or incomplete standard names (e.g., "ISO 4017" without its corresponding "/DIN 933"), and proposes standardized updates.

## Key Features

-   **Finds Inconsistencies:** Scans product family titles for known ISO/DIN mappings and identifies titles that need correction.
-   **Interactive Preview:** Displays a clear, color-coded preview of all proposed changes before any data is modified.
-   **Safe, Transactional Updates:** If the user confirms, all database updates are performed within a single transaction to ensure data integrity. The entire operation will roll back if any part of it fails.
-   **Resilient Connection:** Uses Polly's Retry policy to automatically handle transient database connection errors.
-   **Secure Configuration:** Loads the database connection string from an external `appsettings.json` file, which should be excluded from source control via `.gitignore`.

## Dependencies

-   .NET (Core/5/6+)
-   [Dapper](https://github.com/DapperLib/Dapper)
-   [MySqlConnector](https://github.com/mysql-net/MySqlConnector)
-   [Serilog](https://github.com/serilog/serilog)
-   [Polly](https://github.com/App-vNext/Polly)
-   [Microsoft.Extensions.Configuration](https://www.nuget.org/packages/Microsoft.Extensions.Configuration/)

## Configuration

Before running, you must create an `appsettings.json` file in the project's root directory and add your database connection string.

**`appsettings.json`:**
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "server=YOUR_SERVER;port=3308;user=YOUR_USER;password=YOUR_PASSWORD;database=eshop;AllowUserVariables=true;"
  }
}
