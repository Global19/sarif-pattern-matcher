﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.SarifPatternMatcher.Test.FunctionalTests.AllPlugins
{
    public class BannedApiTests : EndToEndTests
    {
        public BannedApiTests(ITestOutputHelper outputHelper) : base(outputHelper) { }

        [Fact]
        public void EndToEndFunctionalTests_BannedApi()
            => RunAllTests();
    }
}