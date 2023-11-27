import {IWorkAreaService} from "./work-area/work-area-service";
import {IStatusbarService} from "./statusbar/statusbar-service";
import {ITextEditorService} from "@application/editor/text-editor-service";
import {IMainMenuService} from "@application/main-menu/main-menu-service";

export class Workbench {
    constructor(
        @IWorkAreaService readonly workAreaService: IWorkAreaService,
        @IMainMenuService readonly mainMenuService: IMainMenuService,
        @IStatusbarService readonly statusbarService: IStatusbarService,
        @ITextEditorService readonly textEditorService: ITextEditorService,
    ) {
    }
}
