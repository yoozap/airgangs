import * as THREE from 'three';

// ONE WIND, over the whole city.
//
// Everything that should move in it reads the same clock and the same
// direction, so the trees on Rustaveli lean the way the windsock at the
// airport is pointing. The breeze breathes: a slow gust envelope rides on top
// of the steady flow, because a wind that blows at one strength for ever
// reads as a machine, not weather.
//
// Foliage is instanced — tens of thousands of chestnuts in one draw — so the
// sway cannot be done on the CPU. It is a few lines of vertex shader injected
// into the tree materials (makeWindy), driven by uniforms that are SHARED by
// every windy material in the game: one number written per frame moves the
// entire city.

export const wind = {
  // Tbilisi's prevailing wind is up the valley from the north-west.
  dir: new THREE.Vector2(0.80, 0.60).normalize(),
  time: 0,
  gust: 1,      // 0.55 .. 1.3, the slow breathing
  heading: 0,   // rad — where the wind is blowing TO, for things that point
};

// Shared by every windy material. Written once a frame; nothing else to do.
const uniforms = {
  uWindTime: { value: 0 },
  uWindDir: { value: wind.dir.clone() },
  uWindAmp: { value: 1 },
};

export function updateWind(dt) {
  wind.time += dt;
  const t = wind.time;
  // two slow sines beating against each other: gusts that come and go without
  // ever repeating on a count you can hear
  wind.gust = 0.92 + 0.26 * Math.sin(t * 0.19) * Math.sin(t * 0.073 + 1.3);
  // and the direction wanders a few degrees, the way a real breeze does
  const wander = 0.13 * Math.sin(t * 0.11 + 0.7) + 0.06 * Math.sin(t * 0.37);
  wind.heading = Math.atan2(wind.dir.x, wind.dir.y) + wander;
  uniforms.uWindTime.value = t;
  uniforms.uWindAmp.value = wind.gust;
  uniforms.uWindDir.value.set(Math.sin(wind.heading), Math.cos(wind.heading));
}

// The displacement, in the mesh's own space, applied AFTER instancing — so
// every tree leans the same way in the world however it was planted. Doing it
// before instancing would rotate the wind by each tree's random yaw and the
// grove would wobble in all directions instead of leaning downwind.
//
// Height drives it quadratically: the trunk is stiff, the crown is not.
const WIND_VERTEX = /* glsl */`
vec4 mvPosition = vec4( transformed, 1.0 );
#ifdef USE_BATCHING
	mvPosition = batchingMatrix * mvPosition;
#endif
#ifdef USE_INSTANCING
	mvPosition = instanceMatrix * mvPosition;
#endif
{
	#ifdef USE_INSTANCING
		vec3 wBase = instanceMatrix[3].xyz;
	#else
		vec3 wBase = vec3( 0.0 );
	#endif
	float wH = max( mvPosition.y - wBase.y, 0.0 );
	float wPh = dot( wBase.xz, vec2( 0.21, 0.37 ) );
	float wT = uWindTime;
	// the whole crown leaning, two beats so it never looks metronomic
	float wS = sin( wT * 1.15 + wPh ) * 0.70 + sin( wT * 2.31 + wPh * 1.7 ) * 0.30;
	mvPosition.xz += uWindDir * ( wH * wH * uWindBend * uWindAmp * wS );
	// and the leaves themselves, faster and far smaller, across the wind
	float wF = sin( wT * 5.1 + mvPosition.x * 1.7 + mvPosition.z * 1.3 + wPh );
	mvPosition.xz += vec2( -uWindDir.y, uWindDir.x ) * ( wH * uWindFlutter * uWindAmp * wF );
}
mvPosition = modelViewMatrix * mvPosition;
gl_Position = projectionMatrix * mvPosition;
`;

const WIND_HEAD = /* glsl */`
uniform float uWindTime;
uniform vec2 uWindDir;
uniform float uWindAmp;
uniform float uWindBend;
uniform float uWindFlutter;
`;

// Make a material sway. `bend` is how far the crown leans per metre of height
// squared; `flutter` is the small cross-wind shiver of the leaves.
//
// The cache key is constant because every windy material compiles the SAME
// program — the two tuning numbers are uniforms, not literals. Baking them in
// would give each tree species its own shader, and a new program is a frame
// hitch (see the lights-hash storm in the perf notes).
export function makeWindy(material, { bend = 0.0024, flutter = 0.004 } = {}) {
  if (!material || material.userData.windy) return material;
  material.userData.windy = true;
  const prev = material.onBeforeCompile;
  material.onBeforeCompile = (shader, renderer) => {
    prev?.call(material, shader, renderer);
    shader.uniforms.uWindTime = uniforms.uWindTime;
    shader.uniforms.uWindDir = uniforms.uWindDir;
    shader.uniforms.uWindAmp = uniforms.uWindAmp;
    shader.uniforms.uWindBend = { value: bend };
    shader.uniforms.uWindFlutter = { value: flutter };
    shader.vertexShader = WIND_HEAD
      + shader.vertexShader.replace('#include <project_vertex>', WIND_VERTEX);
  };
  material.customProgramCacheKey = () => 'windy';
  return material;
}
