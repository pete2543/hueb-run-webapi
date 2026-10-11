# HUEB rabbit 3D

The mascot is a stylized mesh interpretation of `src/assets/brand/mascot-running.webp`,
not an exact reconstruction of its fur or sculpt. Its head, folded ear, face, running
kit and shoes are geometry. No image billboard or external textures are required.

## Use on another page

```tsx
import { Mascot3D } from "@/components/mascot-3d"

<Mascot3D pose="waving" className="h-80 w-80" />
```

Poses: `idle`, `running`, `waving`. `paused` freezes the current pose. The standalone
component also respects reduced motion and pauses when off screen. Set `interactive`
to enable drag-to-orbit. The canvas has a transparent background, lazy loading and an
image fallback. This component owns a canvas; for an existing React Three Fiber scene,
use `RabbitModel` from `@/components/3d/rabbit-model` instead and pass its pause state.

## Use outside this website

- Asset: `public/models/hueb-rabbit.glb`, served at `/models/hueb-rabbit.glb`.
- Contains PBR materials and three named animation clips: `idle`, `running`, `waving`.
- Y is up, Z is forward. The origin is at the feet. Import as glTF 2.0 in a 3D editor.
- Limbs animate through named group pivots, not a humanoid skeletal rig. Automatic
  humanoid retargeting is not provided.
- Source: `src/lib/3d/hueb-rabbit.ts`. Rebuild with `npm run model:export` using the
  project's Node version (20.9+). The script reloads the result to verify animation clips.

The website and exporter use the same factory. Re-export the GLB after changing it.
