using System.Globalization;
using ExcelDataReader;

namespace TeamHub.Excel;

public static class LegacyExcelWorkbook
{
    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static int encodingProviderRegistered;

    public static bool IsLegacyBinary(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || stream.Length - stream.Position < CompoundFileSignature.Length) return false;
        var position = stream.Position;
        Span<byte> signature = stackalloc byte[CompoundFileSignature.Length];
        var read = stream.Read(signature);
        stream.Position = position;
        return read == CompoundFileSignature.Length && signature.SequenceEqual(CompoundFileSignature);
    }

    public static IExcelDataReader Open(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!IsLegacyBinary(stream)) throw new InvalidDataException("The workbook is not a supported legacy .xls file.");
        if (Interlocked.Exchange(ref encodingProviderRegistered, 1) == 0)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }
        return ExcelReaderFactory.CreateBinaryReader(stream, new ExcelReaderConfiguration
        {
            FallbackEncoding = System.Text.Encoding.GetEncoding(1252),
            LeaveOpen = leaveOpen
        });
    }

    public static string GetCellText(IExcelDataReader reader, int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= reader.FieldCount) return string.Empty;
        var value = reader.GetValue(columnIndex);
        if (value is null or DBNull) return string.Empty;
        return value switch
        {
            DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            double number => FormatNumber(number, reader.GetNumberFormatString(columnIndex)),
            float number => number.ToString(CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string FormatNumber(double number, string? numberFormat)
    {
        if (!string.IsNullOrWhiteSpace(numberFormat)
            && numberFormat.All(character => character == '0')
            && number == Math.Truncate(number))
        {
            return number.ToString(numberFormat, CultureInfo.InvariantCulture);
        }
        return number.ToString("G15", CultureInfo.InvariantCulture);
    }
}
