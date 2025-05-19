// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.DataApiBuilder.Config.ObjectModel;

/// <summary>
/// Holds the global settings used at runtime for MCP Server.
/// </summary>
/// <param name="Enabled">If the MCP Server is enabled.</param>
public record McpRuntimeOptions(bool Enabled = false)
{
};
