package com.jetbrains.rider.plugins.unity.ideaInterop.fileTypes.cg

import com.intellij.codeInsight.completion.CompletionParameters
import com.intellij.codeInsight.completion.CompletionResultSet
import com.intellij.codeInsight.completion.CompletionType
import com.intellij.codeInsight.completion.WordCompletionContributor

class CgCompletionContributor : WordCompletionContributor(){
    override fun fillCompletionVariants(parameters: CompletionParameters, result: CompletionResultSet) {
        if (parameters.completionType == CompletionType.BASIC) {
            addWordCompletionVariants(result, parameters, emptySet<String>())
        }
    }
}