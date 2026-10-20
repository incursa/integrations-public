// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform.Tests.TestUtilities;
using Incursa.Platform.Sqlite.Tests.TestUtilities;

namespace Incursa.Platform.Sqlite.Tests;

[Trait("Category", "Conformance")]
public sealed class SqliteInboxWorkStoreBehaviorTests : InboxWorkStoreBehaviorTestsBase
{
    public SqliteInboxWorkStoreBehaviorTests()
        : base(new SqliteInboxWorkStoreBehaviorHarness())
    {
    }
}
