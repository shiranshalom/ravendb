﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Client.Documents.Operations.TimeSeries
{
    public class TimeSeriesOperation
    {
        private bool _appendSorted;
        private List<AppendOperation> _appends;

        public class AppendOperationEqualityComparer : IEqualityComparer<AppendOperation>
        {
            public static AppendOperationEqualityComparer Instance = new AppendOperationEqualityComparer();

            public bool Equals(AppendOperation x, AppendOperation y)
            {
                if (ReferenceEquals(x, y))
                    return true;
                if (ReferenceEquals(x, null))
                    return false;
                if (ReferenceEquals(y, null))
                    return false;
                if (x.GetType() != y.GetType())
                    return false;
                return x.Timestamp.Equals(y.Timestamp) && x.Tag.Equals(y.Tag, StringComparison.Ordinal);
            }

            public int GetHashCode(AppendOperation obj)
            {
                return HashCode.Combine(obj.Timestamp, obj.Tag);
            }
        }
        private class AppendOperationComparer : IComparer<AppendOperation>
        {
            public static AppendOperationComparer Instance = new AppendOperationComparer();

            public int Compare(AppendOperation x, AppendOperation y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (ReferenceEquals(null, y)) return 1;
                if (ReferenceEquals(null, x)) return -1;
                int timestampComparison = x.Timestamp.CompareTo(y.Timestamp);
                if (timestampComparison != 0) return timestampComparison;
                return string.Compare(x.Tag, y.Tag, StringComparison.Ordinal);
            }
        }

        internal IList<AppendOperation> Appends
        {
            get
            {
                EnsureSorted();
                return _appends;
            }
            private set
            {
                _appends ??= new List<AppendOperation>();
                _appends.AddRange(value);
            }
        }

        private void EnsureSorted()
        {
            if (_appendSorted || _appends == null) 
                return;

            _appends.Sort(AppendOperationComparer.Instance);
            _appendSorted = true;
        }

        internal List<DeleteOperation> Deletes;

        internal List<IncrementOperation> Increments;

        public string Name;

        public void Append(AppendOperation appendOperation)
        {
            _appends ??= new List<AppendOperation>();
            appendOperation.Timestamp = appendOperation.Timestamp.EnsureUtc().EnsureMilliseconds();
            _appends.Add(appendOperation);
        }

        public void Delete(DeleteOperation deleteOperation)
        {
            Deletes ??= new List<DeleteOperation>();
            deleteOperation.To = deleteOperation.To?.EnsureUtc();
            deleteOperation.From = deleteOperation.From?.EnsureUtc();
            Deletes.Add(deleteOperation);
        }

        public void Increment(IncrementOperation incrementOperation)
        {
            Increments ??= new List<IncrementOperation>();
            incrementOperation.Timestamp = incrementOperation.Timestamp.EnsureUtc().EnsureMilliseconds();
            Increments.Add(incrementOperation);
        }

        internal static TimeSeriesOperation Parse(BlittableJsonReaderObject input)
        {
            if (input.TryGet(nameof(Name), out string name) == false || name == null)
                ThrowMissingProperty(nameof(Name));

            var result = new TimeSeriesOperation
            {
                Name = name,
                _appends = new List<AppendOperation>()
            };

            if (input.TryGet(nameof(Appends), out BlittableJsonReaderArray operations) && operations != null)
            {
                foreach (var op in operations)
                {
                    if (!(op is BlittableJsonReaderObject bjro))
                    {
                        ThrowNotBlittableJsonReaderObjectOperation(op);
                        return null; //never hit
                    }

                    var append = AppendOperation.Parse(bjro);

                    result._appends.Add(append);
                }

                result.EnsureSorted();
            }

            if (input.TryGet(nameof(Deletes), out operations) && operations != null)
            {
                result.Deletes = new List<DeleteOperation>();

                foreach (var op in operations)
                {
                    if (!(op is BlittableJsonReaderObject bjro))
                    {
                        ThrowNotBlittableJsonReaderObjectOperation(op);
                        return null; //never hit
                    }

                    result.Deletes.Add(DeleteOperation.Parse(bjro));
                }
            }

            if (input.TryGet(nameof(Increments), out operations) && operations != null)
            {
                result.Increments = new List<IncrementOperation>();

                foreach (var op in operations)
                {
                    if (!(op is BlittableJsonReaderObject bjro))
                    {
                        ThrowNotBlittableJsonReaderObjectOperation(op);
                        return null; //never hit
                    }

                    result.Increments.Add(IncrementOperation.Parse(bjro));
                }
            }

            return result;
        }

        private const string TimeSeriesFormat = "TimeFormat";
        private static readonly long EpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        internal enum TimeFormat
        {
            DotNetTicks,
            UnixTimeInMs,
            UnixTimeInNs
        }

        private static long FromUnixMs(long unixMs)
        {
            return unixMs * 10_000 + EpochTicks;
        }

        private static long FromUnixNs(long unixNs)
        {
            return unixNs / 100 + EpochTicks;
        }

        internal static TimeSeriesOperation ParseForBulkInsert(BlittableJsonReaderObject input)
        {
            if (input.TryGet(nameof(Name), out string name) == false || name == null)
                ThrowMissingProperty(nameof(Name));

            input.TryGet(TimeSeriesFormat, out TimeFormat format);

            var result = new TimeSeriesOperation
            {
                Name = name,
                _appends = new List<AppendOperation>()
            };

            if (input.TryGet(nameof(Appends), out BlittableJsonReaderArray operations) == false || operations == null)
                ThrowMissingProperty(nameof(Appends));


            foreach (var op in operations)
            {
                if (!(op is BlittableJsonReaderArray bjro))
                {
                    ThrowNotBlittableJsonReaderArrayOperation(op);
                    return null; //never hit
                }

                var time = GetLong(bjro[0]);

                switch (format)
                {
                    case TimeFormat.UnixTimeInMs:
                        time = FromUnixMs(time);
                        break;
                    case TimeFormat.UnixTimeInNs:
                        time = FromUnixNs(time);
                        break;
                    case TimeFormat.DotNetTicks:
                        break;
                    default:
                        throw new ArgumentException($"Unknown time-format '{format}'");
                }

                var append = new AppendOperation
                {
                    Timestamp = new DateTime(time)
                };

                var numberOfValues = GetLong(bjro[1]);
                var doubleValues = new double[numberOfValues];

                for (var i = 0; i < numberOfValues; i++)
                {
                    var obj = bjro[i + 2];
                    switch (obj)
                    {
                        case long l:
                            // when we send the number without the decimal point
                            // this is the same as what Convert.ToDouble is doing
                            doubleValues[i] = l;
                            break;

                        case LazyNumberValue lnv:
                            doubleValues[i] = lnv;
                            break;

                        default:
                            doubleValues[i] = Convert.ToDouble(obj);
                            break;
                    }
                }

                append.Values = doubleValues;

                var tagIndex = 2 + numberOfValues;
                if (bjro.Length > tagIndex)
                {
                    if (BlittableJsonReaderObject.ChangeTypeToString(bjro[(int)tagIndex], out string tagAsString) == false)
                        ThrowNotString(bjro[0]);

                    append.Tag = tagAsString;
                }

                result._appends.Add(append);
            }

            result.EnsureSorted();

            return result;

            static long GetLong(object value)
            {
                return value switch
                {
                    long l => l,
                    LazyNumberValue lnv => lnv,
                    _ => throw new NotSupportedException($"Not supported type. Was expecting number, but got '{value}'."),
                };
            }
        }

        private static void ThrowNotBlittableJsonReaderObjectOperation(object op)
        {
            throw new InvalidDataException($"'Operations' should contain items of type BlittableJsonReaderObject only, but got {op.GetType()}");
        }

        private static void ThrowNotBlittableJsonReaderArrayOperation(object op)
        {
            throw new InvalidDataException($"'Appends' should contain items of type BlittableJsonReaderArray only, but got {op.GetType()}");
        }

        private static void ThrowNotString(object obj)
        {
            throw new InvalidDataException($"Expected a string but got: {obj.GetType()}");
        }

        private static void ThrowMissingProperty(string prop)
        {
            throw new InvalidDataException($"Missing '{prop}' property on 'TimeSeriesOperation'");
        }

        public DynamicJsonValue ToJson()
        {
            return new DynamicJsonValue
            {
                [nameof(Name)] = Name,
                [nameof(Appends)] = Appends?.Select(x => x.ToJson()),
                [nameof(Deletes)] = Deletes?.Select(x => x.ToJson()),
                [nameof(Increments)] = Increments?.Select(x => x.ToJson())
            };
        }

        public class AppendOperation 
        {
            public DateTime Timestamp;
            public double[] Values;
            public string Tag;

            internal static AppendOperation Parse(BlittableJsonReaderObject input)
            {
                if (input.TryGet(nameof(Timestamp), out DateTime ts) == false)
                    throw new InvalidDataException($"Missing '{nameof(Timestamp)}' property");

                if (input.TryGet(nameof(Values), out BlittableJsonReaderArray values) == false || values == null)
                    throw new InvalidDataException($"Missing '{nameof(Values)}' property");

                input.TryGet(nameof(Tag), out string tag); // optional

                var doubleValues = new double[values.Length];
                for (int i = 0; i < doubleValues.Length; i++)
                {
                    doubleValues[i] = values.GetByIndex<double>(i);
                }

                var op = new AppendOperation
                {
                    Timestamp = ts,
                    Values = doubleValues,
                    Tag = tag
                };

                return op;
            }

            public DynamicJsonValue ToJson()
            {
                var djv = new DynamicJsonValue
                {
                    [nameof(Timestamp)] = Timestamp,
                    [nameof(Values)] = new DynamicJsonArray(Values.Select(x => (object)x)),
                };

                if (Tag != null)
                    djv[nameof(Tag)] = Tag;

                return djv;
            }
        }

        public class DeleteOperation
        {
            public DateTime? From, To;

            internal static DeleteOperation Parse(BlittableJsonReaderObject input)
            {
                input.TryGet(nameof(From), out DateTime? from); // optional
                input.TryGet(nameof(To), out DateTime? to); // optional

                return new DeleteOperation
                {
                    From = from,
                    To = to
                };
            }

            public DynamicJsonValue ToJson()
            {
                return new DynamicJsonValue
                {
                    [nameof(From)] = From,
                    [nameof(To)] = To
                };
            }
        }

        public class IncrementOperation
        {
            public DateTime Timestamp;
            public long  Delta;

            internal static IncrementOperation Parse(BlittableJsonReaderObject input)
            {
                if (input.TryGet(nameof(Timestamp), out DateTime ts) == false)
                    throw new InvalidDataException($"Missing '{nameof(Timestamp)}' property");

                if(input.TryGet(nameof(Delta), out long delta) == false)
                    throw new InvalidDataException($"Missing '{nameof(Delta)}' property");


                var op = new IncrementOperation
                {
                    Timestamp = ts,
                    Delta = delta
                };

                return op;
            }

            public DynamicJsonValue ToJson()
            {
                return new DynamicJsonValue
                {
                    [nameof(Timestamp)] = Timestamp,
                    [nameof(Delta)] = Delta
                };
            }
        }
    }
}
