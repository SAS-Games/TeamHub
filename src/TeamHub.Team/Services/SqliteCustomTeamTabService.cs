using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace TeamHub.Team;

internal sealed partial class SqliteCustomTeamTabService(TeamDbContext dbContext, IExcelTableSourceReader excelReader) : ICustomTeamTabService
{
    public async Task<IReadOnlyList<CustomTeamTabDto>> ListTabsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.CustomTeamTabs.AsNoTracking()
            .Where(tab => !tab.IsArchived)
            .OrderBy(tab => tab.DisplayOrder)
            .ThenBy(tab => tab.Name)
            .Select(tab => new CustomTeamTabDto
            {
                Id = tab.Id.ToString(),
                Name = tab.Name,
                Slug = tab.Slug,
                DisplayOrder = tab.DisplayOrder
            })
            .ToListAsync(cancellationToken);

    public async Task<CustomTeamTabDto?> GetTabAsync(string slug, CancellationToken cancellationToken = default)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        var tab = await dbContext.CustomTeamTabs.AsNoTracking()
            .SingleOrDefaultAsync(item => !item.IsArchived && item.Slug == normalized, cancellationToken);
        return tab is null ? null : await BuildTabAsync(tab, cancellationToken);
    }

    public async Task<CustomTeamTabDto?> GetTabByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var tabId)) return null;
        var tab = await dbContext.CustomTeamTabs.AsNoTracking()
            .SingleOrDefaultAsync(item => !item.IsArchived && item.Id == tabId, cancellationToken);
        return tab is null ? null : await BuildTabAsync(tab, cancellationToken);
    }

    public async Task<CustomTeamTabDto> SaveTabAsync(SaveCustomTeamTabRequest request, CancellationToken cancellationToken = default)
    {
        var name = Required(request.Name, "Tab name", 80);
        CustomTeamTabRecord? tab = null;
        if (Guid.TryParse(request.Id, out var tabId))
        {
            tab = await dbContext.CustomTeamTabs.SingleOrDefaultAsync(item => item.Id == tabId && !item.IsArchived, cancellationToken)
                ?? throw new KeyNotFoundException("Custom team tab was not found.");
        }

        if (tab is null)
        {
            tab = new CustomTeamTabRecord { Slug = await UniqueSlugAsync(name, cancellationToken) };
            dbContext.CustomTeamTabs.Add(tab);
        }

        tab.Name = name;
        tab.DisplayOrder = Math.Max(0, request.DisplayOrder);
        tab.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (await GetTabByIdAsync(tab.Id.ToString(), cancellationToken))!;
    }

    public async Task ArchiveTabAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var tabId)) return;
        var tab = await dbContext.CustomTeamTabs.SingleOrDefaultAsync(item => item.Id == tabId, cancellationToken);
        if (tab is null) return;
        tab.IsArchived = true;
        tab.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteTabAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var tabId)) return;
        if (!await dbContext.CustomTeamTabs.AnyAsync(item => item.Id == tabId, cancellationToken)) return;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var tableIds = await dbContext.CustomTeamTables
            .Where(item => item.TabId == tabId)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (tableIds.Count > 0)
        {
            await dbContext.CustomTeamRowAudits
                .Where(item => tableIds.Contains(item.TableId))
                .ExecuteDeleteAsync(cancellationToken);
            await dbContext.CustomTeamRows
                .Where(item => tableIds.Contains(item.TableId))
                .ExecuteDeleteAsync(cancellationToken);
            await dbContext.CustomTeamColumns
                .Where(item => tableIds.Contains(item.TableId))
                .ExecuteDeleteAsync(cancellationToken);
            await dbContext.CustomTeamTables
                .Where(item => item.TabId == tabId)
                .ExecuteDeleteAsync(cancellationToken);
        }
        await dbContext.CustomTeamTabs
            .Where(item => item.Id == tabId)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CustomTeamTableDto> SaveTableAsync(SaveCustomTeamTableRequest request, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(request.TabId, out var tabId)
            || !await dbContext.CustomTeamTabs.AnyAsync(item => item.Id == tabId && !item.IsArchived, cancellationToken))
            throw new KeyNotFoundException("Custom team tab was not found.");

        var name = Required(request.Name, "Table name", 100);
        var sourceType = CustomTeamTableSourceTypes.Normalize(request.SourceType);
        var sourceUrl = sourceType == CustomTeamTableSourceTypes.ExcelUrl
            ? Required(request.SourceUrl, "Excel download link", 2048)
            : null;
        if (sourceUrl is not null
            && (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri) || sourceUri.Scheme is not ("http" or "https")))
            throw new ArgumentException("Enter a valid HTTP or HTTPS Excel download link.");
        var worksheet = sourceType == CustomTeamTableSourceTypes.ExcelUrl
            ? Optional(request.SourceWorksheet, 200, "Worksheet name")
            : null;
        var primaryKey = sourceType == CustomTeamTableSourceTypes.ExcelUrl
            ? Required(request.PrimaryKeySourceHeader, "Primary key column", 200)
            : null;
        var headerRow = sourceType == CustomTeamTableSourceTypes.ExcelUrl ? request.SourceHeaderRow : 1;
        if (headerRow is < 1 or > 1000) throw new ArgumentException("Header row must be between 1 and 1,000.");

        CustomTeamTableRecord? table = null;
        if (Guid.TryParse(request.Id, out var tableId))
        {
            table = await dbContext.CustomTeamTables.SingleOrDefaultAsync(
                item => item.Id == tableId && item.TabId == tabId && !item.IsArchived, cancellationToken)
                ?? throw new KeyNotFoundException("Custom team table was not found.");
            var hasRows = await dbContext.CustomTeamRows.AnyAsync(item => item.TableId == table.Id, cancellationToken);
            if (hasRows && !string.Equals(table.SourceType, sourceType, StringComparison.Ordinal))
                throw new InvalidOperationException("The table source cannot be changed after rows have been created.");
            if (table.LastSyncedAtUtc.HasValue
                && !string.Equals(table.PrimaryKeySourceHeader, primaryKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The primary key column cannot be changed after the first successful Excel sync.");
        }

        if (table is null)
        {
            table = new CustomTeamTableRecord { TabId = tabId };
            dbContext.CustomTeamTables.Add(table);
        }
        table.Name = name;
        table.DisplayOrder = Math.Max(0, request.DisplayOrder);
        table.SourceType = sourceType;
        table.SourceUrl = sourceUrl;
        table.SourceWorksheet = worksheet;
        table.SourceHeaderRow = headerRow;
        table.PrimaryKeySourceHeader = primaryKey;
        table.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return await BuildTableAsync(table, cancellationToken);
    }

    public async Task ArchiveTableAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var tableId)) return;
        var table = await dbContext.CustomTeamTables.SingleOrDefaultAsync(item => item.Id == tableId, cancellationToken);
        if (table is null) return;
        table.IsArchived = true;
        table.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<CustomTeamColumnDto> SaveColumnAsync(SaveCustomTeamColumnRequest request, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(request.TableId, out var tableId)
            || !await dbContext.CustomTeamTables.AnyAsync(item =>
                item.Id == tableId
                && !item.IsArchived
                && dbContext.CustomTeamTabs.Any(tab => tab.Id == item.TabId && !tab.IsArchived), cancellationToken))
            throw new KeyNotFoundException("Custom team table was not found.");

        var label = Required(request.Label, "Column label", 80);
        var fieldType = CustomTeamFieldTypes.Normalize(request.FieldType);
        var options = request.Options.Select(item => item.Trim()).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        if (fieldType == CustomTeamFieldTypes.Choice && options.Count == 0)
            throw new ArgumentException("Choice columns require at least one option.");

        CustomTeamColumnRecord? column = null;
        if (Guid.TryParse(request.Id, out var columnId))
        {
            column = await dbContext.CustomTeamColumns.SingleOrDefaultAsync(
                item => item.Id == columnId && item.TableId == tableId && !item.IsArchived, cancellationToken)
                ?? throw new KeyNotFoundException("Custom team column was not found.");
        }

        if (column?.IsSourceColumn == true)
            throw new InvalidOperationException("Excel source columns are managed by synchronization and cannot be edited here.");

        if (column is null)
        {
            column = new CustomTeamColumnRecord
            {
                TableId = tableId,
                Key = await UniqueColumnKeyAsync(tableId, label, cancellationToken)
            };
            dbContext.CustomTeamColumns.Add(column);
        }
        column.Label = label;
        column.FieldType = fieldType;
        column.IsRequired = request.IsRequired;
        column.OptionsJson = JsonSerializer.Serialize(options);
        column.DisplayOrder = Math.Max(0, request.DisplayOrder);
        column.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(column);
    }

    public async Task ArchiveColumnAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var columnId)) return;
        var column = await dbContext.CustomTeamColumns.SingleOrDefaultAsync(item => item.Id == columnId, cancellationToken);
        if (column is null) return;
        if (column.IsSourceColumn)
            throw new InvalidOperationException("Excel source columns are managed by synchronization and cannot be archived.");
        column.IsArchived = true;
        column.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ExcelTableSyncResult> SyncExcelTableAsync(
        string tableId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(tableId, out var parsedTableId)) throw new KeyNotFoundException("Custom team table was not found.");
        var table = await dbContext.CustomTeamTables.SingleOrDefaultAsync(item =>
            item.Id == parsedTableId
            && !item.IsArchived
            && dbContext.CustomTeamTabs.Any(tab => tab.Id == item.TabId && !tab.IsArchived), cancellationToken)
            ?? throw new KeyNotFoundException("Custom team table was not found.");
        if (table.SourceType != CustomTeamTableSourceTypes.ExcelUrl)
            throw new InvalidOperationException("Only Excel-backed tables can be synchronized.");

        ExcelTableSourceData source;
        try
        {
            source = await excelReader.ReadAsync(
                Required(table.SourceUrl, "Excel download link", 2048),
                table.SourceWorksheet,
                table.SourceHeaderRow,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RecordSyncFailureAsync(table.Id, exception.Message, cancellationToken);
            throw new InvalidOperationException($"Excel synchronization failed: {exception.Message}", exception);
        }

        var normalizedActor = NormalizeActor(actor);
        var now = DateTime.UtcNow;
        try
        {
            var primaryKeyHeader = Required(table.PrimaryKeySourceHeader, "Primary key column", 200);
            var actualPrimaryKeyHeader = source.Headers.FirstOrDefault(header =>
                string.Equals(header, primaryKeyHeader, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Primary key column '{primaryKeyHeader}' was not found in worksheet '{source.Worksheet}'.");
            var imported = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var rowNumber = table.SourceHeaderRow;
            foreach (var sourceRow in source.Rows)
            {
                rowNumber++;
                var sourceKey = sourceRow.GetValueOrDefault(actualPrimaryKeyHeader)?.Trim() ?? string.Empty;
                if (sourceKey.Length == 0) throw new InvalidOperationException($"Primary key '{actualPrimaryKeyHeader}' is empty at Excel row {rowNumber}.");
                if (sourceKey.Length > 512) throw new InvalidOperationException($"Primary key at Excel row {rowNumber} exceeds 512 characters.");
                if (!imported.TryAdd(sourceKey, sourceRow))
                    throw new InvalidOperationException($"Duplicate primary key '{sourceKey}' was found in the Excel worksheet.");
            }

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var columns = await dbContext.CustomTeamColumns
                .Where(item => item.TableId == table.Id && !item.IsArchived)
                .OrderBy(item => item.DisplayOrder).ThenBy(item => item.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            var missingHeaders = columns.Where(column => column.IsSourceColumn
                    && !source.Headers.Contains(column.SourceHeader ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                .Select(column => column.SourceHeader)
                .Where(header => !string.IsNullOrWhiteSpace(header))
                .ToList();
            if (missingHeaders.Count > 0)
                throw new InvalidOperationException($"Mapped Excel column(s) are missing: {string.Join(", ", missingHeaders)}.");

            var nextOrder = columns.Count == 0 ? 0 : columns.Max(column => column.DisplayOrder) + 1;
            foreach (var header in source.Headers)
            {
                if (columns.Any(column => column.IsSourceColumn
                    && string.Equals(column.SourceHeader, header, StringComparison.OrdinalIgnoreCase))) continue;
                var column = new CustomTeamColumnRecord
                {
                    TableId = table.Id,
                    Key = await UniqueColumnKeyAsync(table.Id, header, cancellationToken),
                    Label = header,
                    FieldType = CustomTeamFieldTypes.Text,
                    IsRequired = string.Equals(header, actualPrimaryKeyHeader, StringComparison.OrdinalIgnoreCase),
                    DisplayOrder = nextOrder++,
                    IsSourceColumn = true,
                    SourceHeader = header,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                dbContext.CustomTeamColumns.Add(column);
                columns.Add(column);
            }

            var rows = await dbContext.CustomTeamRows
                .Where(item => item.TableId == table.Id && item.SourceKey != null)
                .ToListAsync(cancellationToken);
            var rowsByKey = rows
                .GroupBy(row => row.SourceKey!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderBy(row => row.IsDeleted).First(), StringComparer.OrdinalIgnoreCase);
            var added = 0;
            var updatedCount = 0;
            var restored = 0;

            foreach (var item in imported)
            {
                var isNew = !rowsByKey.TryGetValue(item.Key, out var row);
                if (isNew)
                {
                    row = new CustomTeamRowRecord
                    {
                        TableId = table.Id,
                        SourceKey = item.Key,
                        SourceStatus = CustomTeamRowSourceStatuses.Active,
                        LastSeenAtUtc = now,
                        CreatedBy = normalizedActor,
                        UpdatedBy = normalizedActor,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    };
                    dbContext.CustomTeamRows.Add(row);
                    rows.Add(row);
                    rowsByKey[item.Key] = row;
                    added++;
                }

                if (row is null) throw new InvalidOperationException("The synchronized row could not be created.");
                var wasRestored = row.IsDeleted || row.SourceStatus == CustomTeamRowSourceStatuses.Missing;
                var values = new Dictionary<string, string>(DeserializeValues(row.ValuesJson), StringComparer.OrdinalIgnoreCase);
                foreach (var sourceColumn in columns.Where(column => column.IsSourceColumn))
                    values[sourceColumn.Key] = item.Value.GetValueOrDefault(sourceColumn.SourceHeader ?? string.Empty) ?? string.Empty;
                var serialized = JsonSerializer.Serialize(values);
                var valuesChanged = !JsonEquivalent(row.ValuesJson, serialized);
                if (row.CreatedBy.Length == 0) row.CreatedBy = normalizedActor;
                row.IsDeleted = false;
                row.DeletedBy = null;
                row.DeletedAtUtc = null;
                row.SourceStatus = CustomTeamRowSourceStatuses.Active;
                row.LastSeenAtUtc = now;
                if (wasRestored) restored++;
                if (valuesChanged || wasRestored || isNew)
                {
                    if (isNew)
                    {
                        row.ValuesJson = serialized;
                        AddAudit(row, "Created", normalizedActor, now);
                    }
                    else
                    {
                        row.Version++;
                        row.ValuesJson = serialized;
                        row.UpdatedBy = normalizedActor;
                        row.UpdatedAtUtc = now;
                        AddAudit(row, wasRestored ? "Restored" : "Synchronized", normalizedActor, now);
                        if (!wasRestored) updatedCount++;
                    }
                }
            }

            foreach (var row in rows.Where(row => !row.IsDeleted
                && row.SourceStatus == CustomTeamRowSourceStatuses.Active
                && !imported.ContainsKey(row.SourceKey ?? string.Empty)))
            {
                row.SourceStatus = CustomTeamRowSourceStatuses.Missing;
                row.Version++;
                row.UpdatedBy = normalizedActor;
                row.UpdatedAtUtc = now;
                AddAudit(row, "Missing", normalizedActor, now);
            }

            var missing = rows.Count(row => !row.IsDeleted && row.SourceStatus == CustomTeamRowSourceStatuses.Missing);
            table.SourceWorksheet = source.Worksheet;
            table.PrimaryKeySourceHeader = actualPrimaryKeyHeader;
            table.LastSyncedAtUtc = now;
            table.LastSyncStatus = "Success";
            table.LastSyncMessage = $"Added {added}, updated {updatedCount}, restored {restored}, missing {missing}.";
            table.UpdatedAtUtc = now;
            await SaveRowChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ExcelTableSyncResult(added, updatedCount, missing, restored, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            dbContext.ChangeTracker.Clear();
            await RecordSyncFailureAsync(parsedTableId, exception.Message, cancellationToken);
            throw new InvalidOperationException($"Excel synchronization failed: {exception.Message}", exception);
        }
    }

    public async Task<CustomTeamRowDto> SaveRowAsync(SaveCustomTeamRowRequest request, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(request.TableId, out var tableId)) throw new KeyNotFoundException("Custom team table was not found.");
        var table = await dbContext.CustomTeamTables.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == tableId
                && !item.IsArchived
                && dbContext.CustomTeamTabs.Any(tab => tab.Id == item.TabId && !tab.IsArchived), cancellationToken)
            ?? throw new KeyNotFoundException("Custom team table was not found.");
        var columns = await dbContext.CustomTeamColumns.AsNoTracking()
            .Where(item => item.TableId == table.Id && !item.IsArchived)
            .OrderBy(item => item.DisplayOrder).ThenBy(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        if (columns.Count == 0) throw new InvalidOperationException("Add at least one column before adding rows.");

        var actor = NormalizeActor(request.Actor);
        CustomTeamRowRecord? row = null;
        var action = "Created";
        if (Guid.TryParse(request.Id, out var rowId))
        {
            row = await dbContext.CustomTeamRows.SingleOrDefaultAsync(
                item => item.Id == rowId && item.TableId == tableId && !item.IsDeleted, cancellationToken)
                ?? throw new KeyNotFoundException("Custom team row was not found.");
            if (row.Version != request.Version) throw new InvalidOperationException("This row was changed by another user. Reload the page and try again.");
            row.Version++;
            action = "Updated";
        }
        else if (table.SourceType == CustomTeamTableSourceTypes.ExcelUrl)
        {
            throw new InvalidOperationException("Rows in an Excel-backed table are created by synchronization.");
        }

        Dictionary<string, string> values;
        if (table.SourceType == CustomTeamTableSourceTypes.ExcelUrl)
        {
            var localColumns = columns.Where(column => !column.IsSourceColumn).ToList();
            if (localColumns.Count == 0) throw new InvalidOperationException("Add a Team Hub column before editing synchronized rows.");
            values = new Dictionary<string, string>(DeserializeValues(row!.ValuesJson), StringComparer.OrdinalIgnoreCase);
            foreach (var item in ValidateValues(localColumns, request.Values)) values[item.Key] = item.Value;
        }
        else
        {
            values = ValidateValues(columns, request.Values);
        }

        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new CustomTeamRowRecord
            {
                TableId = tableId,
                SourceStatus = CustomTeamRowSourceStatuses.Manual,
                CreatedBy = actor,
                CreatedAtUtc = now
            };
            dbContext.CustomTeamRows.Add(row);
        }
        row.ValuesJson = JsonSerializer.Serialize(values);
        row.UpdatedBy = actor;
        row.UpdatedAtUtc = now;
        AddAudit(row, action, actor, now);
        await SaveRowChangesAsync(cancellationToken);
        return ToDto(row);
    }

    public async Task RemoveRowAsync(string tableId, string rowId, int version, string actor, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(tableId, out var parsedTableId) || !Guid.TryParse(rowId, out var parsedRowId)) return;
        var table = await dbContext.CustomTeamTables.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == parsedTableId
            && !item.IsArchived
            && dbContext.CustomTeamTabs.Any(tab => tab.Id == item.TabId && !tab.IsArchived), cancellationToken);
        if (table is null) return;
        var row = await dbContext.CustomTeamRows.SingleOrDefaultAsync(
            item => item.Id == parsedRowId && item.TableId == parsedTableId && !item.IsDeleted, cancellationToken);
        if (row is null) return;
        if (table.SourceType == CustomTeamTableSourceTypes.ExcelUrl
            && row.SourceStatus != CustomTeamRowSourceStatuses.Missing)
            throw new InvalidOperationException("Remove the record from Excel first, synchronize, and then remove the missing record from Team Hub.");
        if (row.Version != version) throw new InvalidOperationException("This row was changed by another user. Reload the page and try again.");
        var now = DateTime.UtcNow;
        var normalizedActor = string.IsNullOrWhiteSpace(actor) ? "Unknown" : actor.Trim();
        row.Version++;
        row.IsDeleted = true;
        row.DeletedBy = normalizedActor;
        row.DeletedAtUtc = now;
        row.UpdatedBy = normalizedActor;
        row.UpdatedAtUtc = now;
        AddAudit(row, "Deleted", normalizedActor, now);
        await SaveRowChangesAsync(cancellationToken);
    }

    private async Task<CustomTeamTabDto> BuildTabAsync(CustomTeamTabRecord tab, CancellationToken cancellationToken)
    {
        var tables = await dbContext.CustomTeamTables.AsNoTracking()
            .Where(item => item.TabId == tab.Id && !item.IsArchived)
            .OrderBy(item => item.DisplayOrder).ThenBy(item => item.Name)
            .ToListAsync(cancellationToken);
        var result = ToDto(tab);
        var resultTables = new List<CustomTeamTableDto>();
        foreach (var table in tables) resultTables.Add(await BuildTableAsync(table, cancellationToken));
        result.Tables = resultTables;
        return result;
    }

    private async Task<CustomTeamTableDto> BuildTableAsync(CustomTeamTableRecord table, CancellationToken cancellationToken)
    {
        var columns = await dbContext.CustomTeamColumns.AsNoTracking()
            .Where(item => item.TableId == table.Id && !item.IsArchived)
            .OrderBy(item => item.DisplayOrder).ThenBy(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var rows = await dbContext.CustomTeamRows.AsNoTracking()
            .Where(item => item.TableId == table.Id && !item.IsDeleted)
            .OrderBy(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return new CustomTeamTableDto
        {
            Id = table.Id.ToString(),
            TabId = table.TabId.ToString(),
            Name = table.Name,
            DisplayOrder = table.DisplayOrder,
            SourceType = table.SourceType,
            SourceUrl = table.SourceUrl,
            SourceWorksheet = table.SourceWorksheet,
            SourceHeaderRow = table.SourceHeaderRow,
            PrimaryKeySourceHeader = table.PrimaryKeySourceHeader,
            LastSyncedAtUtc = table.LastSyncedAtUtc,
            LastSyncStatus = table.LastSyncStatus,
            LastSyncMessage = table.LastSyncMessage,
            Columns = columns.Select(column => ToDto(column, table.PrimaryKeySourceHeader)).ToList(),
            Rows = rows.Select(ToDto).ToList()
        };
    }

    private static Dictionary<string, string> ValidateValues(
        IReadOnlyList<CustomTeamColumnRecord> columns,
        IReadOnlyDictionary<string, string?> posted)
    {
        var source = new Dictionary<string, string?>(posted, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            var value = source.GetValueOrDefault(column.Key)?.Trim() ?? string.Empty;
            if (column.IsRequired && value.Length == 0) throw new ArgumentException($"{column.Label} is required.");
            if (value.Length > 4000) throw new ArgumentException($"{column.Label} cannot exceed 4,000 characters.");
            if (value.Length > 0) ValidateValue(column, value);
            result[column.Key] = value;
        }
        return result;
    }

    private static void ValidateValue(CustomTeamColumnRecord column, string value)
    {
        var valid = column.FieldType switch
        {
            CustomTeamFieldTypes.Number => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
            CustomTeamFieldTypes.Date => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            CustomTeamFieldTypes.Boolean => bool.TryParse(value, out _),
            CustomTeamFieldTypes.Url => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https",
            CustomTeamFieldTypes.Choice => DeserializeOptions(column.OptionsJson).Contains(value, StringComparer.OrdinalIgnoreCase),
            _ => true
        };
        if (!valid) throw new ArgumentException($"Enter a valid value for {column.Label}.");
    }

    private async Task SaveRowChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException("This row was changed by another user. Reload the page and try again.", exception);
        }
    }

    private void AddAudit(CustomTeamRowRecord row, string action, string actor, DateTime now) =>
        dbContext.CustomTeamRowAudits.Add(new CustomTeamRowAuditRecord
        {
            RowId = row.Id,
            TableId = row.TableId,
            Action = action,
            ValuesJson = row.ValuesJson,
            Version = row.Version,
            Actor = actor,
            CreatedAtUtc = now
        });

    private async Task<string> UniqueSlugAsync(string name, CancellationToken cancellationToken)
    {
        var root = SlugPart().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (root.Length == 0) root = "team-tab";
        root = root[..Math.Min(60, root.Length)].TrimEnd('-');
        var candidate = root;
        for (var suffix = 2; await dbContext.CustomTeamTabs.AnyAsync(item => item.Slug == candidate, cancellationToken); suffix++)
            candidate = $"{root}-{suffix}";
        return candidate;
    }

    private async Task<string> UniqueColumnKeyAsync(Guid tableId, string label, CancellationToken cancellationToken)
    {
        var root = SlugPart().Replace(label.Trim().ToLowerInvariant(), "-").Trim('-');
        if (root.Length == 0) root = "column";
        root = root[..Math.Min(60, root.Length)].TrimEnd('-');
        var candidate = root;
        for (var suffix = 2; await dbContext.CustomTeamColumns.AnyAsync(item => item.TableId == tableId && item.Key == candidate, cancellationToken); suffix++)
            candidate = $"{root}-{suffix}";
        return candidate;
    }

    private async Task RecordSyncFailureAsync(Guid tableId, string message, CancellationToken cancellationToken)
    {
        var table = await dbContext.CustomTeamTables.SingleOrDefaultAsync(item => item.Id == tableId, cancellationToken);
        if (table is null) return;
        table.LastSyncStatus = "Failed";
        table.LastSyncMessage = message.Length <= 1000 ? message : message[..1000];
        table.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool JsonEquivalent(string left, string right) =>
        new Dictionary<string, string>(DeserializeValues(left), StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(
                new Dictionary<string, string>(DeserializeValues(right), StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase));

    private static string NormalizeActor(string? actor) =>
        string.IsNullOrWhiteSpace(actor) ? "Unknown" : actor.Trim();

    private static string? Optional(string? value, int maxLength, string label)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > maxLength) throw new ArgumentException($"{label} cannot exceed {maxLength} characters.");
        return trimmed;
    }

    private static string Required(string? value, string label, int maxLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw new ArgumentException($"{label} is required.");
        if (trimmed.Length > maxLength) throw new ArgumentException($"{label} cannot exceed {maxLength} characters.");
        return trimmed;
    }

    private static IReadOnlyList<string> DeserializeOptions(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static IReadOnlyDictionary<string, string> DeserializeValues(string json) =>
        new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [], StringComparer.OrdinalIgnoreCase);

    private static CustomTeamTabDto ToDto(CustomTeamTabRecord tab) => new()
    {
        Id = tab.Id.ToString(), Name = tab.Name, Slug = tab.Slug, DisplayOrder = tab.DisplayOrder
    };

    private static CustomTeamColumnDto ToDto(CustomTeamColumnRecord column, string? primaryKeySourceHeader = null) => new()
    {
        Id = column.Id.ToString(), TableId = column.TableId.ToString(), Key = column.Key, Label = column.Label,
        FieldType = column.FieldType, IsRequired = column.IsRequired, Options = DeserializeOptions(column.OptionsJson), DisplayOrder = column.DisplayOrder,
        IsSourceColumn = column.IsSourceColumn, SourceHeader = column.SourceHeader,
        IsPrimaryKey = column.IsSourceColumn && string.Equals(column.SourceHeader, primaryKeySourceHeader, StringComparison.OrdinalIgnoreCase)
    };

    private static CustomTeamRowDto ToDto(CustomTeamRowRecord row) => new()
    {
        Id = row.Id.ToString(), TableId = row.TableId.ToString(), Values = DeserializeValues(row.ValuesJson), Version = row.Version,
        CreatedBy = row.CreatedBy, UpdatedBy = row.UpdatedBy, CreatedAtUtc = row.CreatedAtUtc, UpdatedAtUtc = row.UpdatedAtUtc,
        SourceKey = row.SourceKey, SourceStatus = row.SourceStatus, LastSeenAtUtc = row.LastSeenAtUtc
    };

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugPart();
}
