"""CAD-independent input contract for the educational drawing macro."""

from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, FiniteFloat, model_validator

Coordinate = Annotated[FiniteFloat, Field(ge=0, le=1200)]
Point = tuple[Coordinate, Coordinate]


class Entity(BaseModel):
    model_config = ConfigDict(extra="forbid")
    kind: Literal["line", "polyline", "circle", "arc", "text", "dimension"]
    points: Annotated[list[Point], Field(min_length=1, max_length=1500)]
    closed: bool = False
    width: Annotated[FiniteFloat, Field(ge=0, le=5)] = 0
    radius: Annotated[FiniteFloat, Field(ge=0, le=600)] = 0
    offset: Annotated[FiniteFloat, Field(ge=-600, le=600)] = 0
    align: Literal["horizontal", "vertical", "distance"] = "distance"
    text: Annotated[str, Field(max_length=240)] = ""

    @model_validator(mode="after")
    def check_geometry(self):
        count = len(self.points)
        fixed = {"line": 2, "circle": 1, "arc": 3, "text": 1, "dimension": 2}
        if self.kind == "polyline":
            if count < (3 if self.closed else 2):
                raise ValueError("A polyline has too few vertices.")
        elif count != fixed[self.kind] or self.closed:
            raise ValueError(f"Unexpected point count or closed flag for {self.kind}.")
        if self.kind == "circle" and self.radius <= 0:
            raise ValueError("Circle radius must be positive.")
        if self.kind == "text" and not self.text.strip():
            raise ValueError("Text cannot be empty.")
        if len(set(self.points)) < count and (self.kind != "polyline" or self.closed):
            raise ValueError("Repeated points do not define a valid primitive.")
        if any(a == b for a, b in zip(self.points, self.points[1:])):
            raise ValueError("Adjacent points must be distinct.")
        if self.kind == "arc":
            (x1, y1), (x2, y2), (x3, y3) = self.points
            if abs((x2 - x1) * (y3 - y1) - (x3 - x1) * (y2 - y1)) < 1e-8:
                raise ValueError("Three non-collinear points are required for an arc.")
        if self.kind == "dimension":
            coordinate = {"horizontal": 0, "vertical": 1}.get(self.align)
            if coordinate is not None and self.points[0][coordinate] == self.points[1][coordinate]:
                raise ValueError("Projected dimension length must be positive.")
        return self


class Sheet(BaseModel):
    model_config = ConfigDict(extra="forbid")
    name: Annotated[str, Field(min_length=1, max_length=60)]
    width: Annotated[FiniteFloat, Field(ge=10, le=1200)] = 297
    height: Annotated[FiniteFloat, Field(ge=10, le=1200)] = 210
    entities: Annotated[list[Entity], Field(min_length=1, max_length=6000)]

    @model_validator(mode="after")
    def check_sheet(self):
        if not self.name.strip():
            raise ValueError("Sheet name cannot be blank.")
        if any(x > self.width or y > self.height for entity in self.entities for x, y in entity.points):
            raise ValueError(f"Points are outside sheet {self.name}.")
        return self


class Drawing(BaseModel):
    model_config = ConfigDict(extra="forbid")
    sheets: Annotated[list[Sheet], Field(min_length=1, max_length=12)]

    @model_validator(mode="after")
    def check_drawing(self):
        names = {sheet.name.strip().casefold() for sheet in self.sheets}
        if len(names) != len(self.sheets):
            raise ValueError("Sheet names must be distinct.")
        count = sum(
            len(entity.points) - (0 if entity.closed else 1) if entity.kind == "polyline" else 1
            for sheet in self.sheets for entity in sheet.entities
        )
        if count > 6000:
            raise ValueError("The drawing exceeds 6000 generated entities.")
        return self
