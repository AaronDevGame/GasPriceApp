using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

// Read and split locally: counting pages does not require an extraction request.
public sealed class DoePdfPages : IDisposable
{
    private readonly MemoryStream _stream;
    private readonly PdfDocument _document;

    public DoePdfPages(byte[] pdf)
    {
        _stream = new MemoryStream(pdf, writable: false);
        try
        {
            _document = PdfReader.Open(_stream, PdfDocumentOpenMode.Import);
            if (_document.PageCount is < 1 or > 100)
            {
                _document.Dispose();
                throw new InvalidDataException("DOE PDF must contain between 1 and 100 pages.");
            }
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidDataException)
        {
            _stream.Dispose();
            throw new InvalidDataException("DOE PDF could not be opened or has an invalid page count.", ex);
        }
    }

    public int Count => _document.PageCount;

    public byte[] ReadPage(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > Count)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        using var page = new PdfDocument();
        page.AddPage(_document.Pages[pageNumber - 1]);
        using var output = new MemoryStream();
        page.Save(output, closeStream: false);
        return output.ToArray();
    }

    public void Dispose()
    {
        _document.Dispose();
        _stream.Dispose();
    }
}
