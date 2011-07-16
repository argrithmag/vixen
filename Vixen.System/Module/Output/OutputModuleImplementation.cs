﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vixen.Common;

namespace Vixen.Module.Output {
	[TypeOfModule("Output")]
	class OutputModuleImplementation : ModuleImplementation<IOutputModuleInstance> {
		public OutputModuleImplementation()
			: base(new OutputModuleManagement(), new OutputModuleRepository()) {
		}
	}
}