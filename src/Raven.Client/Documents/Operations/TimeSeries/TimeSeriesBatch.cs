﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Client.Documents.Operations.TimeSeries
{
    public class TimeSeriesBatch
    {
        public List<TimeSeriesOperation> Documents = new List<TimeSeriesOperation>();
    }

    public class TimeSeriesOperation
    {
        public List<AppendOperation> Appends;

        public List<RemoveOperation> Removals;

        public string DocumentId;

        internal static TimeSeriesOperation Parse(BlittableJsonReaderObject input)
        {
            if (input.TryGet(nameof(DocumentId), out string docId) == false || docId == null)
                ThrowMissingDocumentId();

            var result = new TimeSeriesOperation
            {
                DocumentId = docId
            };

            if (input.TryGet(nameof(Appends), out BlittableJsonReaderArray operations) && operations != null)
            {
                result.Appends = new List<AppendOperation>();

                foreach (var op in operations)
                {
                    if (!(op is BlittableJsonReaderObject bjro))
                    {
                        ThrowNotBlittableJsonReaderObjectOperation(op);
                        return null; //never hit
                    }

                    result.Appends.Add(AppendOperation.Parse(bjro));
                }
            }

            if (input.TryGet(nameof(Removals), out operations) && operations != null)
            {
                result.Removals = new List<RemoveOperation>();

                foreach (var op in operations)
                {
                    if (!(op is BlittableJsonReaderObject bjro))
                    {
                        ThrowNotBlittableJsonReaderObjectOperation(op);
                        return null; //never hit
                    }

                    result.Removals.Add(RemoveOperation.Parse(bjro));
                }
            }

            return result;
        }

        private static void ThrowNotBlittableJsonReaderObjectOperation(object op)
        {
            throw new InvalidDataException($"'Operations' should contain items of type BlittableJsonReaderObject only, but got {op.GetType()}");
        }

        private static void ThrowMissingDocumentId()
        {
            throw new InvalidDataException($"Missing '{nameof(DocumentId)}' property on 'TimeSeries'");
        }

        public DynamicJsonValue ToJson()
        {
            return new DynamicJsonValue
            {
                [nameof(DocumentId)] = DocumentId,
                [nameof(Appends)] = Appends?.Select(x => x.ToJson()),
                [nameof(Removals)] = Removals?.Select(x => x.ToJson())
            };
        }

        public class AppendOperation
        {
            public string Name;
            public DateTime Timestamp;
            public double[] Values;
            public string Tag;

            internal static AppendOperation Parse(BlittableJsonReaderObject input)
            {
                if (input.TryGet(nameof(Name), out string name) == false || name == null)
                    throw new InvalidDataException($"Missing '{nameof(Name)}' property");

                if (input.TryGet(nameof(Tag), out string tag) == false || name == null)
                    throw new InvalidDataException($"Missing '{nameof(Tag)}' property");

                if (input.TryGet(nameof(Timestamp), out DateTime ts) == false || name == null)
                    throw new InvalidDataException($"Missing '{nameof(Timestamp)}' property");

                if (input.TryGet(nameof(Values), out BlittableJsonReaderArray values) == false || name == null)
                    throw new InvalidDataException($"Missing '{nameof(Values)}' property");

                var doubleValues = new double[values.Length];
                for (int i = 0; i < doubleValues.Length; i++)
                {
                    doubleValues[i] = values.GetByIndex<double>(i);
                }

                var op = new AppendOperation
                {
                    Name = name,
                    Timestamp = ts,
                    Values = doubleValues,
                    Tag = tag
                };

                return op;
            }

            public DynamicJsonValue ToJson()
            {
                return new DynamicJsonValue
                {
                    [nameof(Name)] = Name,
                    [nameof(Timestamp)] = Timestamp,
                    [nameof(Tag)] = Tag,
                    [nameof(Values)] = new DynamicJsonArray(Values.Select(x => (object)x)),
                };
            }
        }

        public class RemoveOperation
        {
            public string Name;
            public DateTime From, To;

            internal static RemoveOperation Parse(BlittableJsonReaderObject input)
            {
                if (input.TryGet(nameof(Name), out string name) == false || name == null)
                    throw new InvalidDataException($"Missing '{nameof(Name)}' property");

                if (input.TryGet(nameof(From), out DateTime from) == false || name == null)
                    throw new InvalidDataException($"Missing '{nameof(From)}' property");

                if (input.TryGet(nameof(To), out DateTime to) == false || name == null)
                    throw new InvalidDataException($"Missing '{nameof(To)}' property");

                var op = new RemoveOperation
                {
                    Name = name,
                    From = from,
                    To = to
                };

                return op;
            }

            public DynamicJsonValue ToJson()
            {
                return new DynamicJsonValue
                {
                    [nameof(Name)] = Name,
                    [nameof(From)] = From,
                    [nameof(To)] = To,
                };
            }
        }
    }
}
