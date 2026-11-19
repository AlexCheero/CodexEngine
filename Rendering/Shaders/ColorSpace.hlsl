#ifndef CODEX_COLOR_SPACE
#define CODEX_COLOR_SPACE
// Colours are authored and stored as sRGB. Light is added, blended and filtered as linear radiance,
// and the screen pass encodes the result once for the display.
float3 Linear(float3 c) {
    c = saturate(c);
    // A close fit to the sRGB curve, cheaper than its exact pow.
    return c*(c*(c*.305306011+.682171111)+.012522878);
}
float3 Encode(float3 c) {
    c = max(c,0);
    return lerp(1.055*pow(c,1/2.4)-.055,12.92*c,step(c,.0031308));
}
#endif
