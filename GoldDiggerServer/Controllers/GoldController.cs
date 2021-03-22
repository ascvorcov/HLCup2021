using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace GoldServer.Controllers
{
    [ApiController]
    public class GoldController : ControllerBase
    {
        static int license;
        private readonly ILogger<GoldController> _logger;

        private static readonly Random rnd = new Random();
        private static byte[,] map;

        static GoldController()
        {
          map = new byte[3500,3500];
          byte level = 1;
          int limitSize = 490000 / 2;
          int nextLimit = limitSize;
          for (int i = 1; i <= 490000; ++i)
          {
            var x = rnd.Next(3500);
            var y = rnd.Next(3500);

            if (i >= nextLimit)
            {
              limitSize /= 2;
              nextLimit = i + limitSize;
              level <<= 1;
              if (level == 0) { level= 1; nextLimit = int.MaxValue; }
            }
            map[y,x] |= level;
          }
        }

        public GoldController(ILogger<GoldController> logger)
        {
          _logger = logger;
        }

        [HttpGet("health-check")]
        public IActionResult HealthCheck()
        {
          return Ok();
        }

        [HttpPost("licenses")]
        public IActionResult IssueLicense([FromBody] int[] coins)
        {
          //_logger.LogInformation($"Issuing license for {coins.Length} coins");
          var allow = coins.Length switch 
          {
            0 => 3,
            <=5 => 5,
            <=10 => 10,
            <=21 => 25,
            > 21 => 45
          };
          return Ok(new License { id = license++, digAllowed = allow, digUsed = 0 });
        }

        [HttpPost("explore")]
        public IActionResult Explore([FromBody] Area area)
        {
          if (area.posX + area.sizeX >= 3500 || area.posY + area.sizeY >= 3500) 
            throw new Exception("Bad coordinates");
          //_logger.LogInformation($"Exploring area {area?.sizeX}x{area?.sizeY} at {area?.posX}:{area?.posY}");
            int ret = 0;
          for (int x = area.posX; x < area.posX + area.sizeX; ++x)
            for (int y = area.posY; y < area.posY + area.sizeY; ++y)
              if (map[y,x] > 0) ret++;

          return Ok(new Report { area = area, amount = ret });
        }

        [HttpPost("dig")]
        public IActionResult Dig([FromBody] Dig dig)
        {
          //_logger.LogInformation($"Digging at {dig?.posX}:{dig?.posY}, depth {dig.depth}, with license {dig.licenseID}");

          return (map[dig.posY,dig.posY] & (1 << (dig.depth-1))) > 0 ? Ok(new[] { (dig.depth).ToString() }) : (IActionResult)NotFound();
        }

        [HttpPost("cash")]
        public IActionResult Cash([FromBody] string treasure)
        {
          //_logger.LogInformation($"Cashing treasure {treasure}");
          var t = int.Parse(treasure);
          var size = t switch
          {
            1 => 4,
            2 => 9,
            3 => 15,
            4 => 23,
            5 => 27,
            _ => 30
          };
          return Ok(Enumerable.Repeat(size,size).ToArray());
        }
    }

    public sealed class License 
    {
        public int id { get; set; }
    
        public int digAllowed { get; set; }
    
        public int digUsed { get; set; }
    }
    
    public sealed class Area 
    {
        public int posX { get; set; }
    
        public int posY { get; set; }
    
        public int? sizeX { get; set; }
    
        public int? sizeY { get; set; }
    }
    
    public sealed class Report 
    {
        public Area area { get; set; }
    
        public int amount { get; set; }
    }
    
    public sealed class Dig 
    {
        public int licenseID { get; set; }
    
        public int posX { get; set; }
    
        public int posY { get; set; }
    
        public int depth { get; set; }
    }
}
