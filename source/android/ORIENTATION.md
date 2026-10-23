# Sensor orientation protocol (Android sender 0.2.0)

The H.264 stream remains in its sensor-buffer orientation. The sender does not
rotate or restart the encoder when the device moves. The PC can rotate decoded
frames before processing and publishing them.

`/api/status` adds:

| Field | Meaning |
| --- | --- |
| `orientationSupported` | Android has a usable orientation sensor. |
| `orientationKnown` | At least one stable quadrant has been obtained. False means keep the manual/fallback orientation. |
| `deviceOrientation` | Stable physical orientation in `OrientationEventListener` convention: 0, 90, 180, or 270 degrees. 0 is natural portrait on a Pixel; 90 has the left side at the top; 180 is upside down; 270 has the right side at the top. |
| `sensorOrientation` | `SENSOR_ORIENTATION` of the selected physical rear lens, also exposed per lens in `/api/cameras`. |
| `rotationDegrees` | Clockwise correction to apply to the unrotated image buffer: `(sensorOrientation + deviceOrientation) % 360`. It is 0 before a stable reading exists. |

For a rear sensor mounted at 90 degrees, physical quadrants 0/90/180/270 map to
clockwise buffer corrections 90/180/270/0. Mirroring is separate; rear-camera
orientation does not require a horizontal flip.

The listener uses the device sensor rather than display rotation, so Android's
screen-rotation lock does not prevent 180-degree detection. Changes enter a new
quadrant only beyond a 10-degree dead band and after 250 ms near its centre.
When Android reports `ORIENTATION_UNKNOWN` for a near-flat device, it cancels
pending changes and retains the last useful quadrant. `orientationKnown` stays
true in that case; it means a last stable orientation exists, not that gravity
currently resolves an angle. Listening stops while the activity is paused.

Where supported, capture requests explicitly select
`SCALER_ROTATE_AND_CROP_NONE`, so display compatibility transformations do not
invalidate the metadata. Capture dimensions, encoder configuration and camera
stream lifecycle otherwise remain unchanged.

The correction follows the captured-image example in Android's
[JPEG_ORIENTATION API](https://developer.android.com/reference/android/hardware/camera2/CaptureRequest#JPEG_ORIENTATION)
and the raw-buffer explanation in the
[camera-orientation guide](https://developer.android.com/develop/devices/chromeos/learn/camera-orientation#jpeg_orientation).
The API defines clockwise correction relative to the image buffer and adds the
clockwise sensor-listener angle for rear cameras. This is not the same operation
as a view/display coordinate transform. See also
[OrientationEventListener](https://developer.android.com/reference/android/view/OrientationEventListener#onOrientationChanged(int))
and
[SENSOR_ORIENTATION](https://developer.android.com/reference/android/hardware/camera2/CameraCharacteristics#SENSOR_ORIENTATION).

Build validation is possible without a phone. Final physical validation should
check both landscape directions (a 180-degree turn) and portrait, with the screen
rotation lock enabled, and confirm the PC output stays upright.

During Activity recreation or an APK update, the new loopback server retries an
occupied port every 125 ms for up to eight seconds, allowing the old Activity to
release it. Failed listener sockets are closed between attempts. Destroying the
Activity cancels retrying and closes its listener and accepted connections, so
queued HTTP work cannot retain the old port or connection sockets.
