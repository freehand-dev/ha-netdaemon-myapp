using NetDaemon.HassModel.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DNSHosting.Models
{
    public class DNSHostingConfig
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public List<DomainConfig> Domains { get; set; } = new List<DomainConfig>(); 
    }

    public class DomainConfig
    {
        public string? Domain { get; set; }
        public List<MapsConfig> Maps { get; set; } = new List<MapsConfig>();    
    }

    public class MapsConfig
    {
        public Entity? Entity { get; set; }
        public List<string> Subdomains { get; set; } = new List<string>();
    }
}
