namespace PTZCameraOperator.Models
{
    /// <summary>
    /// Represents a stored PTZ preset position with coordinates
    /// </summary>
    public class PresetPosition
    {
        public string Name { get; set; } = "Preset";
        public int PresetNumber { get; set; } = 0;
        public float Pan { get; set; } = 0.0f;
        public float Tilt { get; set; } = 0.0f;
        public float Zoom { get; set; } = 0.0f;
        public bool UseCoordinates { get; set; } = false; // If true, use coordinates; if false, try preset recall
        public string? CameraId { get; set; } // Store which camera this preset belongs to
    }
}
