// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

namespace HyperVMcp.Engine;

/// <summary>
/// Shared timeout clamping for MCP tool handlers.
/// Prevents agents from setting excessively long wait times that would
/// cause the MCP request to time out at the transport level.
/// </summary>
public static class TimeoutHelper
{
    /// <summary>
    /// Maximum allowed timeout for any MCP tool wait parameter (seconds).
    /// Hard timeouts (backend kill timers) are not subject to this cap.
    /// </summary>
    public const int MaxTimeoutSeconds = 45;

    /// <summary>
    /// Clamp a requested timeout to the maximum allowed value.
    /// Returns the clamped value and whether clamping occurred.
    /// </summary>
    public static (int timeout, bool wasClamped) ClampTimeout(int requested)
    {
        if (requested > MaxTimeoutSeconds)
            return (MaxTimeoutSeconds, true);
        return (requested, false);
    }
}
