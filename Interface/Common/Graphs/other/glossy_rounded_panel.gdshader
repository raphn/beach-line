shader_type canvas_item;
render_mode unshaded;

uniform vec2 panel_size = vec2(100.0, 30.0);
uniform float corner_radius : hint_range(0.0, 32.0) = 8.0;

float rounded_edge(vec2 position, vec2 half_size, float radius) {
	vec2 edge = abs(position) - half_size + radius;
	return length(max(edge, vec2(0.0))) + min(max(edge.x, edge.y), 0.0) - radius;
}

void fragment() {
	vec2 half_size = panel_size * 0.5;
	vec2 position = UV * panel_size - half_size;
	float radius = min(corner_radius, min(half_size.x, half_size.y));
	float edge = rounded_edge(position, half_size - vec2(0.5), radius);
	float shape = 1.0 - smoothstep(-0.5, 0.5, edge);
	float inner = 1.0 - smoothstep(-1.5, -0.5, edge);
	float gloss = 1.0 - smoothstep(0.0, 0.52, UV.y);
	vec3 base = mix(vec3(0.10, 0.12, 0.15), COLOR.rgb, 0.42);
	base *= mix(0.75, 0.96, UV.y);
	vec3 fill = mix(base, vec3(0.93, 0.96, 1.0), gloss * 0.3);
	vec3 rim = mix(base * 0.7, base + vec3(0.24), gloss);
	COLOR = vec4(mix(rim, fill, inner), shape * COLOR.a);
}
