import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { InspectorSurface } from "./InspectorSurface";
import { Workbench } from "./Workbench";

const surface = new URLSearchParams(window.location.search).get("surface");

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {surface === "inspector" ? <InspectorSurface /> : <Workbench />}
  </StrictMode>
);
