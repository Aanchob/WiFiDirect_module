using System;

namespace direct_module.Models
{
    public class User
    {
        public string DeviceId { get; set; } = "";

        public string MachineName { get; set; } = "";

        public string DisplayName { get; set; } = "";

        public DateTime CreatedAt { get; set; }
    }
}