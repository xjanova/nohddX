using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NohddX.Boot.Http;

namespace NohddX.Api.Controllers;

[ApiController]
[Route("api/boot")]
[AllowAnonymous] // PXE / iPXE clients have no header support
public class BootController : ControllerBase
{
    private readonly BootEndpointHandler _bootHandler;

    public BootController(BootEndpointHandler bootHandler)
    {
        _bootHandler = bootHandler;
    }

    [HttpGet("{mac}.ipxe")]
    [Produces("text/plain")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK, "text/plain")]
    public async Task<IActionResult> GetBootScript(string mac, CancellationToken ct = default)
    {
        var (script, contentType) = await _bootHandler.HandleBootRequestAsync(mac, ct);
        return Content(script, contentType);
    }
}
