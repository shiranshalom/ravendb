using System.Collections.Generic;
using System.Threading.Tasks;
using FastTests;
using Raven.Client.Documents.Operations.Configuration;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_21946 : RavenTestBase
    {
        public RavenDB_21946(ITestOutputHelper output) : base(output)
        {
        }

        [RavenFact(RavenTestCategory.Configuration)]
        public async Task ShouldUpdateLastModifiedTimeAfterConflictResolutionSave()
        {
            using (var store = GetDocumentStore())
            {
                var collectionByScript = new Dictionary<string, ScriptResolver>
                {
                    {
                        "Users", new ScriptResolver
                       {
                            Script = "Script1"
                        }
                    }
                };

                // configure conflict resolver with an initial script for the specified collection
                await store.Maintenance.Server.SendAsync(new ModifyConflictSolverOperation(store.Database, collectionByScript, resolveToLatest: false));

                var conflictSolver = await store.Maintenance.SendAsync(new GetConflictSolverConfigurationOperation());

                Assert.False(conflictSolver.IsEmpty());
                Assert.False(conflictSolver.ResolveToLatest);
                Assert.Single(conflictSolver.ResolveByCollection);
                Assert.True(conflictSolver.ResolveByCollection.TryGetValue("Users", out var scriptResolver1));

                // wait to confirm LastModifiedTime remains unchanged over time
                await Task.Delay(1000);

                var conflictSolver2 = await store.Maintenance.SendAsync(new GetConflictSolverConfigurationOperation());

                // ensure LastModifiedTime remains the same since no changes have been made
                Assert.True(conflictSolver2.ResolveByCollection.TryGetValue("Users", out var scriptResolver2));
                Assert.Equal(scriptResolver1.LastModifiedTime, scriptResolver2.LastModifiedTime);

                // update the conflict solver configuration with a new script and check that LastModifiedTime is updated
                collectionByScript["Users"] = new ScriptResolver { Script = "Script2" };
                await store.Maintenance.Server.SendAsync(new ModifyConflictSolverOperation(store.Database, collectionByScript, resolveToLatest: false));

                var conflictSolver3 = await store.Maintenance.SendAsync(new GetConflictSolverConfigurationOperation());

                Assert.True(conflictSolver3.ResolveByCollection.TryGetValue("Users", out var scriptResolver3));
                Assert.NotEqual(scriptResolver2.LastModifiedTime, scriptResolver3.LastModifiedTime);
            }
        }
    }
}
