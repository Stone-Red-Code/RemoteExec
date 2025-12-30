using Microsoft.AspNetCore.Mvc;

using RemoteExec.Server.Hubs;

namespace RemoteExec.Server.Controllers;

/// <summary>
/// Controller for handling assembly upload requests from clients.
/// </summary>
[ApiController]
[Route("/")]
public class AssemblyController(ILogger<AssemblyController> logger) : ControllerBase
{
    /// <summary>
    /// Receives an assembly binary from a client in response to an assembly request.
    /// </summary>
    /// <param name="requestId">The unique identifier for the assembly request.</param>
    /// <returns>An action result indicating success or failure.</returns>
    [HttpPost("provide-assembly")]
    public async Task<IActionResult> ProvideAssembly([FromQuery] Guid requestId)
    {
        try
        {
            using MemoryStream ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            byte[] assemblyBytes = ms.ToArray();

            logger.LogInformation("Received assembly for request {RequestId}, size: {Size} bytes", requestId, assemblyBytes.Length);

            await RemoteExecutionHub.ProvideAssembly(requestId, assemblyBytes);

            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error providing assembly for request {RequestId}", requestId);
            return StatusCode(500, ex.Message);
        }
    }
}