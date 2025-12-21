using Microsoft.AspNetCore.Mvc;

using RemoteExec.Server.Hubs;

namespace RemoteExec.Server.Controllers;

[ApiController]
[Route("/")]
public class AssemblyController(ILogger<AssemblyController> logger) : ControllerBase
{
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