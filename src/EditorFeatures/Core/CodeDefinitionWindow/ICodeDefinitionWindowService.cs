﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CodeAnalysis.CodeDefinitionWindow;

internal interface ICodeDefinitionWindowService
{
    Task<bool> IsWindowOpenAsync(CancellationToken cancellationToken);
    Task SetContextAsync(ImmutableArray<CodeDefinitionWindowLocation> locations, CancellationToken cancellationToken);
}
