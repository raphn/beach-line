# Note-value icons

Original flat symbols, authored for BeachLine. Warm white (#f4f1e8), transparent backgrounds; no external artwork or font dependency.

Values: whole, half, quarter, eighth, sixteenth, thirty-second. Each has a corresponding `-rest` icon. Whole and half rests include a short reference line to distinguish hanging and sitting forms.

Individual SVGs use 128 x 128 canvases. `all-note-values.svg` is the editable source sheet: notes on top, rests below. `all-note-values.png` is the transparent 2x raster sheet (1536 x 512). The JPG is a preview on a dark background, not a transparent UI texture.

Existing note `.atlastex` paths are preserved; matching `*-rest.atlastex` resources are provided. Each atlas region is 256 x 256. SVGs can also be used directly as Godot textures.

The beat editor also uses the supplemental sixty-fourth.svg and sixty-fourth-rest.svg icons directly (four flags/hooks). These are not part of the six-column atlas sheet.
