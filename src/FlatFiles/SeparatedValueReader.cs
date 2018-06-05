﻿using System;
using System.IO;
using System.Threading.Tasks;
using FlatFiles.Resources;

namespace FlatFiles
{
    /// <summary>
    /// Extracts records from a file that has values separated by a separator token.
    /// </summary>
    public sealed class SeparatedValueReader : IReader
    {
        private readonly SeparatedValueRecordParser parser;
        private readonly SeparatedValueSchemaSelector schemaSelector;
        private readonly Metadata metadata;
        private object[] values;
        private bool endOfFile;
        private bool hasError;

        /// <summary>
        /// Initializes a new SeparatedValueReader with no schema.
        /// </summary>
        /// <param name="reader">A reader over the separated value document.</param>
        /// <param name="options">The options controlling how the separated value document is read.</param>
        /// <exception cref="ArgumentNullException">The reader is null.</exception>
        public SeparatedValueReader(TextReader reader, SeparatedValueOptions options = null)
            : this(reader, null, options, false)
        {
        }

        /// <summary>
        /// Initializes a new SeparatedValueReader with the given schema.
        /// </summary>
        /// <param name="reader">A reader over the separated value document.</param>
        /// <param name="schema">The schema of the separated value document.</param>
        /// <param name="options">The options controlling how the separated value document is read.</param>
        /// <exception cref="ArgumentNullException">The reader is null.</exception>
        /// <exception cref="ArgumentNullException">The schema is null.</exception>
        public SeparatedValueReader(TextReader reader, SeparatedValueSchema schema, SeparatedValueOptions options = null)
            : this(reader, schema, options, true)
        {
        }

        /// <summary>
        /// Initializes a new SeparatedValueReader with the given schema.
        /// </summary>
        /// <param name="reader">A reader over the separated value document.</param>
        /// <param name="schemaSelector">The schema selector configured to determine the schema dynamically.</param>
        /// <param name="options">The options controlling how the separated value document is read.</param>
        /// <exception cref="ArgumentNullException">The reader is null.</exception>
        /// <exception cref="ArgumentNullException">The schema selector is null.</exception>
        public SeparatedValueReader(TextReader reader, SeparatedValueSchemaSelector schemaSelector, SeparatedValueOptions options = null)
            : this(reader, null, options, false)
        {
            this.schemaSelector = schemaSelector ?? throw new ArgumentNullException(nameof(schemaSelector));
        }

        private SeparatedValueReader(TextReader reader, SeparatedValueSchema schema, SeparatedValueOptions options, bool hasSchema)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }
            if (hasSchema && schema == null)
            {
                throw new ArgumentNullException(nameof(schema));
            }
            if (options == null)
            {
                options = new SeparatedValueOptions();
            }
            if (options.RecordSeparator == options.Separator)
            {
                throw new ArgumentException(SharedResources.SameSeparator, nameof(options));
            }
            RetryReader retryReader = new RetryReader(reader);
            this.parser = new SeparatedValueRecordParser(retryReader, options);
            this.metadata = new Metadata()
            {
                Schema = hasSchema ? schema : null,
                Options = this.parser.Options
            };
        }

        /// <summary>
        /// Raised when a record is read but before its columns are parsed.
        /// </summary>
        public event EventHandler<SeparatedValueRecordReadEventArgs> RecordRead;

        /// <summary>
        /// Raised when an error occurs while processing a record.
        /// </summary>
        public event EventHandler<ProcessingErrorEventArgs> Error;

        /// <summary>
        /// Gets the names of the columns found in the file.
        /// </summary>
        /// <returns>The names.</returns>
        public SeparatedValueSchema GetSchema()
        {
            if (schemaSelector != null)
            {
                return null;
            }
            handleSchema();
            if (metadata.Schema == null)
            {
                throw new InvalidOperationException(SharedResources.SchemaNotDefined);
            }
            return metadata.Schema;
        }

        ISchema IReader.GetSchema()
        {
            return GetSchema();
        }

        /// <summary>
        /// Gets the schema being used by the parser.
        /// </summary>
        /// <returns>The schema being used by the parser.</returns>
        public async Task<SeparatedValueSchema> GetSchemaAsync()
        {
            if (schemaSelector != null)
            {
                return null;
            }
            await handleSchemaAsync();
            if (metadata.Schema == null)
            {
                throw new InvalidOperationException(SharedResources.SchemaNotDefined);
            }
            return metadata.Schema;
        }

        async Task<ISchema> IReader.GetSchemaAsync()
        {
            var schema = await GetSchemaAsync();
            return schema;
        }

        /// <summary>
        /// Attempts to read the next record from the stream.
        /// </summary>
        /// <returns>True if the next record was read or false if all records have been read.</returns>
        public bool Read()
        {
            if (hasError)
            {
                throw new InvalidOperationException(SharedResources.ReadingWithErrors);
            }
            handleSchema();
            try
            {
                values = parsePartitions();
                if (values == null)
                {
                    return false;
                }
                else
                {
                    ++metadata.LogicalRecordCount;
                    return true;
                }
            }
            catch (FlatFileException)
            {
                hasError = true;
                throw;
            }
        }

        private void handleSchema()
        {
            if (metadata.RecordCount != 0)
            {
                return;
            }
            if (!parser.Options.IsFirstRecordSchema)
            {
                return;
            }
            if (schemaSelector != null || metadata.Schema != null)
            {
                skip();
                return;
            }
            string[] columnNames = readNextRecord();
            metadata.Schema = new SeparatedValueSchema();
            foreach (string columnName in columnNames)
            {
                StringColumn column = new StringColumn(columnName);
                metadata.Schema.AddColumn(column);
            }
        }

        private object[] parsePartitions()
        {
            string[] rawValues = readWithFilter();
            while (rawValues != null)
            {
                var schema = getSchema(rawValues);
                if (schema != null && hasWrongNumberOfColumns(schema, rawValues))
                {
                    processError(new RecordProcessingException(metadata.RecordCount, SharedResources.SeparatedValueRecordWrongNumberOfColumns));
                }
                else
                {
                    object[] values = parseValues(schema, rawValues);
                    if (values != null)
                    {
                        return values;
                    }
                }
                rawValues = readWithFilter();
            }
            return null;
        }

        private string[] readWithFilter()
        {
            string[] rawValues = readNextRecord();
            while (rawValues != null && isSkipped(rawValues))
            {
                rawValues = readNextRecord();
            }
            return rawValues;
        }

        /// <summary>
        /// Attempts to read the next record from the stream.
        /// </summary>
        /// <returns>True if the next record was read or false if all records have been read.</returns>
        public async ValueTask<bool> ReadAsync()
        {
            if (hasError)
            {
                throw new InvalidOperationException(SharedResources.ReadingWithErrors);
            }
            await handleSchemaAsync();
            try
            {
                values = await parsePartitionsAsync();
                if (values == null)
                {
                    return false;
                }
                else
                {
                    ++metadata.LogicalRecordCount;
                    return true;
                }
            }
            catch (FlatFileException)
            {
                hasError = true;
                throw;
            }
        }

        private async Task handleSchemaAsync()
        {
            if (metadata.RecordCount != 0)
            {
                return;
            }
            if (!parser.Options.IsFirstRecordSchema)
            {
                return;
            }
            if (metadata.Schema != null)
            {
                await skipAsync();
                return;
            }
            string[] columnNames = await readNextRecordAsync();
            metadata.Schema = new SeparatedValueSchema();
            foreach (string columnName in columnNames)
            {
                StringColumn column = new StringColumn(columnName);
                metadata.Schema.AddColumn(column);
            }
        }

        private async Task<object[]> parsePartitionsAsync()
        {
            string[] rawValues = await readWithFilterAsync();
            while (rawValues != null)
            {
                var schema = getSchema(rawValues);
                if (schema != null && hasWrongNumberOfColumns(schema, rawValues))
                {
                    processError(new RecordProcessingException(metadata.RecordCount, SharedResources.SeparatedValueRecordWrongNumberOfColumns));
                }
                else
                {
                    object[] values = parseValues(schema, rawValues);
                    if (values != null)
                    {
                        return values;
                    }
                }
                rawValues = await readWithFilterAsync();
            }
            return null;
        }

        private SeparatedValueSchema getSchema(string[] rawValues)
        {
            return schemaSelector == null ? metadata.Schema : schemaSelector.GetSchema(rawValues);
        }

        private bool hasWrongNumberOfColumns(SeparatedValueSchema schema, string[] values)
        {
            return values.Length + schema.ColumnDefinitions.MetadataCount < schema.ColumnDefinitions.PhysicalCount;
        }

        private async Task<string[]> readWithFilterAsync()
        {
            string[] rawValues = await readNextRecordAsync();
            while (rawValues != null && isSkipped(rawValues))
            {
                rawValues = await readNextRecordAsync();
            }
            return rawValues;
        }

        private bool isSkipped(string[] values)
        {
            if (RecordRead == null)
            {
                return false;
            }
            var e = new SeparatedValueRecordReadEventArgs(values);
            RecordRead(this, e);
            return e.IsSkipped;
        }

        private object[] parseValues(SeparatedValueSchema schema, string[] rawValues)
        {
            if (schema == null)
            {
                return rawValues;
            }
            try
            {
                var metadata = schemaSelector == null ? this.metadata : new Metadata()
                {
                    Schema = schema,
                    Options = this.metadata.Options,
                    RecordCount = this.metadata.RecordCount,
                    LogicalRecordCount = this.metadata.LogicalRecordCount
                };
                return schema.ParseValues(metadata, rawValues);
            }
            catch (FlatFileException exception)
            {
                processError(new RecordProcessingException(metadata.RecordCount, SharedResources.InvalidRecordConversion, exception));
                return null;
            }
        }

        /// <summary>
        /// Attempts to skip the next record from the stream.
        /// </summary>
        /// <returns>True if the next record was skipped or false if all records have been read.</returns>
        /// <remarks>The previously parsed values remain available.</remarks>
        public bool Skip()
        {
            if (hasError)
            {
                throw new InvalidOperationException(SharedResources.ReadingWithErrors);
            }
            handleSchema();
            bool result = skip();
            return result;
        }

        private bool skip()
        {
            string[] rawValues = readNextRecord();
            return rawValues != null;
        }

        /// <summary>
        /// Attempts to skip the next record from the stream.
        /// </summary>
        /// <returns>True if the next record was skipped or false if all records have been read.</returns>
        /// <remarks>The previously parsed values remain available.</remarks>
        public async ValueTask<bool> SkipAsync()
        {
            if (hasError)
            {
                throw new InvalidOperationException(SharedResources.ReadingWithErrors);
            }
            await handleSchemaAsync();
            bool result = await skipAsync();
            return result;
        }

        private async ValueTask<bool> skipAsync()
        {
            string[] rawValues = await readNextRecordAsync();
            return rawValues != null;
        }

        private void processError(RecordProcessingException exception)
        {
            if (Error != null)
            {
                var args = new ProcessingErrorEventArgs(exception);
                Error(this, args);
                if (args.IsHandled)
                {
                    return;
                }
            }
            throw exception;
        }

        private string[] readNextRecord()
        {
            if (parser.IsEndOfStream())
            {
                endOfFile = true;
                values = null;
                return null;
            }
            try
            {
                string[] results = parser.ReadRecord();
                ++metadata.RecordCount;
                return results;
            }
            catch (SeparatedValueSyntaxException exception)
            {
                throw new RecordProcessingException(metadata.RecordCount, SharedResources.InvalidRecordFormatNumber, exception);
            }
        }

        private async Task<string[]> readNextRecordAsync()
        {
            if (await parser.IsEndOfStreamAsync())
            {
                endOfFile = true;
                values = null;
                return null;
            }
            try
            {
                string[] results = await parser.ReadRecordAsync();
                ++metadata.RecordCount;
                return results;
            }
            catch (SeparatedValueSyntaxException exception)
            {
                throw new RecordProcessingException(metadata.RecordCount, SharedResources.InvalidRecordFormatNumber, exception);
            }
        }

        /// <summary>
        /// Gets the values for the current record.
        /// </summary>
        /// <returns>The values of the current record.</returns>
        public object[] GetValues()
        {
            if (hasError)
            {
                throw new InvalidOperationException(SharedResources.ReadingWithErrors);
            }
            if (metadata.RecordCount == 0)
            {
                throw new InvalidOperationException(SharedResources.ReadNotCalled);
            }
            if (endOfFile)
            {
                throw new InvalidOperationException(SharedResources.NoMoreRecords);
            }
            object[] copy = new object[values.Length];
            Array.Copy(values, copy, values.Length);
            return copy;
        }

        private class Metadata : IProcessMetadata
        {
            public SeparatedValueSchema Schema { get; set; }

            ISchema IProcessMetadata.Schema
            {
                get { return Schema; }
            }

            public SeparatedValueOptions Options { get; set; }

            IOptions IProcessMetadata.Options
            {
                get { return Options; }
            }

            public int RecordCount { get; set; }

            public int LogicalRecordCount { get; set; }
        }
    }
}
