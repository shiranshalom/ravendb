﻿using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit.Sdk;

namespace Tests.Infrastructure;

public class RavenExternalReplicationAttribute : DataAttribute
{
    private readonly RavenDatabaseMode _source;
    private readonly RavenDatabaseMode _destination;
    private object[] _data;

    public RavenExternalReplicationAttribute(
        RavenDatabaseMode source = RavenDatabaseMode.Single,
        RavenDatabaseMode destination = RavenDatabaseMode.Single
    )
    {
        _source = source;
        _destination = destination;
    }

    public RavenExternalReplicationAttribute(
        RavenDatabaseMode source = RavenDatabaseMode.Single,
        RavenDatabaseMode destination = RavenDatabaseMode.Single,
        params object[] data
    ) : this ( source, destination )
    {
        _data = data;
    }

    public override IEnumerable<object[]> GetData(MethodInfo testMethod)
    {
        foreach (var (_, dstOptions) in RavenDataAttribute.GetOptions(_destination))
        foreach (var (__, srcOptions) in RavenDataAttribute.GetOptions(_source))
        {
            if (_data == null || _data.Length == 0)
            {
                yield return new object[] { dstOptions, srcOptions };
                continue;
            }

            yield return new object[] { dstOptions, srcOptions }.Concat(_data).ToArray();
        }
    }
}
