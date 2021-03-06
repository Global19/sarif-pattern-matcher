﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.CodeAnalysis.Sarif.PatternMatcher.Plugins.Security.HelpersUtiliesAndExtensions
{
    public static class DictionaryExtensions
    {
        public static bool TryGetNonEmptyValue<TKey>(this Dictionary<TKey, string> dictionary, TKey key, out string value)
        {
            return dictionary.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value);
        }
    }
}
