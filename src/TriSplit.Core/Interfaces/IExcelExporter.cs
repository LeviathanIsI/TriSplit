using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TriSplit.Core.Models;
using TriSplit.Core.Processors;

namespace TriSplit.Core.Interfaces;

public interface IExcelExporter
{
    Task<string> WriteContactsAsync(string outputDirectory, string fileName, IEnumerable<ContactRecord> records, CancellationToken cancellationToken);
    Task<string> WritePhonesAsync(string outputDirectory, string fileName, IEnumerable<PhoneRecord> records, CancellationToken cancellationToken);
    Task<string> WritePropertiesAsync(string outputDirectory, string fileName, IEnumerable<PropertyRecord> records, IReadOnlyList<string> additionalFieldOrder, CancellationToken cancellationToken);
}
